<#
.SYNOPSIS
    Proves, against the built AS2.ModApi.dll, that this framework only observes the game.

.DESCRIPTION
    README.md makes two claims to users: that the framework cannot change what the game does, and
    that it reads a score and can never set one. Both used to be promises kept by review. This
    script turns them into facts checked by a machine, and tools\pack.ps1 runs it before it archives
    anything, so a release cannot ship a violation.

    Six rules, in the order they matter:

      1. No Broadcast. The framework listens on the game's Messenger bus and never speaks on it.
      2. No field write into Assembly-CSharp (stfld / stsfld).
      3. No reflective write. Rule 2 alone is not enough, because AccessTools takes a type *name*
         as a string and a reflective SetValue leaves no type reference for a scan to find.
      4. No call into Assembly-CSharp outside a reviewed allowlist. Property getters are NOT
         exempt: the game has getters with side effects -- SongSync.get_Playhead broadcasts
         "SongStartedPlaying" -- so "it is only a getter" is not a safety argument here.
      5. Harmony patches stay postfix-only, and stay funnelled through the single helper.
      6. No public member exposes a type from Assembly-CSharp, and no public type inherits one.
         This is what keeps the game events read-only for a subscriber, rather than only for the
         framework: a mod is handed immutable copies, so it has nothing to write back through.

    Every failure prints the method and the IL offset, so a violation is one line to read.

.PARAMETER Assembly
    The DLL to check. Defaults to build\plugins\AS2.ModApi.dll.

.PARAMETER CecilPath
    Mono.Cecil.dll. Defaults to the pack staging area, then the installed game's BepInEx.
#>

[CmdletBinding()]
param(
    [string]$Assembly = '',
    [string]$CecilPath = '',
    [string]$AudiosurfDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Assembly) { $Assembly = Join-Path $repoRoot 'build\plugins\AS2.ModApi.dll' }
if (-not (Test-Path $Assembly)) { throw "Assembly not found: $Assembly. Build AS2.ModApi first." }

# ---- Mono.Cecil ---------------------------------------------------------------------------------
#
# Not a NuGet dependency, matching the rest of this repo. Cecil ships inside BepInEx, so it is
# already on disk in both places this script ever runs: the pack staging area during a release, and
# the installed game during development.

if (-not $CecilPath) {
    if (-not $AudiosurfDir) {
        $AudiosurfDir = if ($env:AudiosurfDir) { $env:AudiosurfDir }
                        else { 'C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2' }
    }
    $candidates = @(
        (Join-Path $repoRoot 'build\stage\BepInEx\core\Mono.Cecil.dll'),
        (Join-Path $AudiosurfDir 'BepInEx\core\Mono.Cecil.dll')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $CecilPath = $c; break } }
}
if (-not $CecilPath -or -not (Test-Path $CecilPath)) {
    throw "Mono.Cecil.dll not found. Pass -CecilPath, or -AudiosurfDir pointing at the game."
}

Add-Type -Path $CecilPath | Out-Null

# ---- The reviewed allowlists --------------------------------------------------------------------
#
# Keep these short, and give every entry a reason. An allowlist that grows without comment proves
# nothing. "Type::Method" against the game's own assembly.

$AllowedGameCalls = @{
    # AS2Input.Lock(). The framework's one deliberate write into the game, and the reason that class
    # exists: inputLockCount is a per-instance field, so a mod that unlocks the wrong UIManager
    # silently releases every other mod's lock. See InputLock.cs.
    'UIManager::Exists'          = 'AS2Input.Lock guards on this before touching the manager'
    'UIManager::get_instance'    = 'AS2Input.Lock captures the exact manager it locked'
    'UIManager::LockInput'       = 'AS2Input.Lock'
    'UIManager::UnlockInput'     = 'AS2Input.Lock handle disposal, on the same instance'

    # MessengerBridge. Subscribing is the whole point; nothing here broadcasts, and rule 1 checks it.
    'Messenger::AddListener'     = 'MessengerBridge, arity 0'
    'Messenger`1::AddListener'   = 'MessengerBridge, arity 1'
    'Messenger`2::AddListener'   = 'MessengerBridge, arity 2'
    'Messenger`3::AddListener'   = 'MessengerBridge, arity 3'

    # AS2Events.CurrentKey. All three are accessors over a private field and all are pure reads.
    'CodeEditor::get_skinPath'   = 'AS2Events.CurrentKey fallback'
    'CodeEditor::get_modPath'    = 'AS2Events.CurrentKey fallback'
    'Mode::get_relativePath'     = 'AS2Events.CurrentKey reads the selected mode script path'

    # Constructing the game's own delegate types, so MessengerBridge can hand AddListener a handler
    # of the shape it demands. A delegate constructor runs no game code -- it stores a target and a
    # method pointer -- so these are not calls into the game in any meaningful sense. They are
    # listed rather than exempted by a rule, because "it is only a constructor" is the kind of
    # blanket exemption this allowlist exists to avoid.
    'Callback::.ctor'            = 'MessengerBridge, arity 0 handler'
    'Callback`1::.ctor'          = 'MessengerBridge, arity 1 handler'
    'Callback`2::.ctor'          = 'MessengerBridge, arity 2 handler'
    'Callback`3::.ctor'          = 'MessengerBridge, arity 3 handler'
}

# Reflective writes. Cheap to spell out, and the list is what rule 3 means.
$ReflectiveSetters = @(
    'FieldInfo::SetValue', 'FieldInfo::SetValueDirect',
    'PropertyInfo::SetValue',
    'AccessTools::FieldRefAccess', 'AccessTools::StaticFieldRefAccess',
    'AccessTools::PropertySetter', 'AccessTools::DeclaredPropertySetter',
    'Traverse::SetValue', 'Traverse::SetField', 'Traverse::SetProperty'
)

$PatchHelper = 'AS2.ModApi.Patches::Patch'

# ---- Cecil helpers ------------------------------------------------------------------------------

function Get-ElementTypeRef($t) {
    while ($null -ne $t) {
        if ($t -is [Mono.Cecil.GenericInstanceType]) { $t = $t.ElementType; continue }
        if ($t -is [Mono.Cecil.TypeSpecification])   { $t = $t.ElementType; continue }
        break
    }
    return $t
}

function Get-ScopeName($typeRef) {
    $t = Get-ElementTypeRef $typeRef
    if ($null -eq $t) { return '' }
    if ($null -eq $t.Scope) { return '' }
    return $t.Scope.Name
}

function Test-IsGameType($typeRef) {
    return (Get-ScopeName $typeRef) -eq 'Assembly-CSharp'
}

function Get-MemberKey($memberRef) {
    $t = Get-ElementTypeRef $memberRef.DeclaringType
    if ($null -eq $t) { return $memberRef.Name }
    return "$($t.Name)::$($memberRef.Name)"
}

function Get-AllTypes($module) {
    $out = New-Object System.Collections.Generic.List[object]
    $queue = New-Object System.Collections.Generic.Queue[object]
    foreach ($t in $module.Types) { $queue.Enqueue($t) }
    while ($queue.Count -gt 0) {
        $t = $queue.Dequeue()
        $out.Add($t)
        foreach ($n in $t.NestedTypes) { $queue.Enqueue($n) }
    }
    return ,$out
}

function Get-MethodLabel($method) {
    return "$($method.DeclaringType.FullName)::$($method.Name)"
}

# ---- Findings -----------------------------------------------------------------------------------

$findings = New-Object System.Collections.Generic.List[string]
function Add-Finding($rule, $where, $what) {
    $findings.Add(("  [rule {0}] {1}`n            {2}" -f $rule, $where, $what))
}

$module = [Mono.Cecil.ModuleDefinition]::ReadModule($Assembly)
$allTypes = Get-AllTypes $module

Write-Host "==> Checking $([IO.Path]::GetFileName($Assembly)) against six invariants" -ForegroundColor Cyan

# ---- Rules 1-5: walk every instruction -----------------------------------------------------------

$patchCallSites = New-Object System.Collections.Generic.List[object]

foreach ($type in $allTypes) {
    foreach ($method in $type.Methods) {
        if (-not $method.HasBody) { continue }
        $label = Get-MethodLabel $method

        foreach ($ins in $method.Body.Instructions) {
            $code = $ins.OpCode.Code.ToString()
            $at = '{0} at IL_{1:x4}' -f $label, $ins.Offset

            switch -Regex ($code) {

                '^(Stfld|Stsfld)$' {
                    $f = $ins.Operand
                    if ($null -ne $f -and (Test-IsGameType $f.DeclaringType)) {
                        Add-Finding 2 $at "writes the game field $(Get-MemberKey $f)"
                    }
                    continue
                }

                '^(Call|Callvirt|Newobj)$' {
                    $m = $ins.Operand
                    if ($null -eq $m) { continue }
                    $key = Get-MemberKey $m

                    # Rule 3 -- reflective writes, wherever they are declared.
                    if ($ReflectiveSetters -contains $key) {
                        Add-Finding 3 $at "calls the reflective setter $key"
                        continue
                    }

                    if (Test-IsGameType $m.DeclaringType) {
                        # Rule 1 -- the framework never broadcasts.
                        if ($m.Name -eq 'Broadcast') {
                            Add-Finding 1 $at "broadcasts on the game's Messenger bus ($key)"
                            continue
                        }
                        # Rule 4 -- everything else into the game needs a reviewed allowlist entry.
                        if (-not $AllowedGameCalls.ContainsKey($key)) {
                            Add-Finding 4 $at "calls $key, which is not on the reviewed allowlist in tools\verify-invariants.ps1"
                        }
                        continue
                    }

                    # Rule 5 -- collect Harmony patch sites for the dedicated pass below.
                    if ($key -eq 'Harmony::Patch' -or $key -eq 'Harmony::PatchAll') {
                        $patchCallSites.Add([pscustomobject]@{
                            Method = $method; Instruction = $ins; Label = $label; Key = $key
                        })
                    }
                    continue
                }
            }
        }
    }
}

# ---- Rule 5: postfixes only ----------------------------------------------------------------------
#
# Two halves. Every Harmony.Patch call must come from the single helper, so there is one place to
# read; and at that call the prefix, transpiler and finalizer arguments must be literal null, so no
# game method can be skipped, rewritten or have its result replaced.
#
# The argument check needs to know which pushed value lands in which parameter, so it simulates the
# evaluation stack forward through the method body. The stack is cleared at every branch target and
# every exception handler boundary, which is where the CLI guarantees it is empty anyway. Anything
# this cannot follow is reported as a failure rather than waved through: fail closed.

function Get-PopCount($ins) {
    $b = $ins.OpCode.StackBehaviourPop.ToString()
    switch ($b) {
        'Pop0' { return 0 }
        'Pop1' { return 1 }
        'Popi' { return 1 }
        'Popref' { return 1 }
        'Pop1_pop1' { return 2 }
        'Popi_pop1' { return 2 }
        'Popi_popi' { return 2 }
        'Popi_popi8' { return 2 }
        'Popi_popr4' { return 2 }
        'Popi_popr8' { return 2 }
        'Popref_pop1' { return 2 }
        'Popref_popi' { return 2 }
        'Popi_popi_popi' { return 3 }
        'Popref_popi_popi' { return 3 }
        'Popref_popi_popi8' { return 3 }
        'Popref_popi_popr4' { return 3 }
        'Popref_popi_popr8' { return 3 }
        'Popref_popi_popref' { return 3 }
        # -1 means "the stack is empty after this instruction", not "give up". `leave` unwinds out
        # of a protected region and discards whatever was on the stack, and so does `ret`. Treating
        # those as unfollowable used to make rule 5 unprovable for the one method it checks, since
        # Patches.Patch wraps its body in a try/catch and therefore ends every path with `leave`.
        'PopAll' { return -1 }
        'Varpop' {
            $code = $ins.OpCode.Code.ToString()
            if ($code -eq 'Ret') { return -1 }
            $m = $ins.Operand
            if ($null -eq $m) { return -2 }
            $n = $m.Parameters.Count
            if ($code -ne 'Newobj' -and $m.HasThis) { $n++ }
            return $n
        }
        default { return -2 }   # genuinely unrecognised: fail closed
    }
}

function Get-PushCount($ins) {
    $b = $ins.OpCode.StackBehaviourPush.ToString()
    switch ($b) {
        'Push0' { return 0 }
        'Push1' { return 1 }
        'Pushi' { return 1 }
        'Pushi8' { return 1 }
        'Pushr4' { return 1 }
        'Pushr8' { return 1 }
        'Pushref' { return 1 }
        'Push1_push1' { return 2 }
        'Varpush' {
            $code = $ins.OpCode.Code.ToString()
            if ($code -eq 'Newobj') { return 1 }
            $m = $ins.Operand
            if ($null -eq $m) { return -1 }
            if ($m.ReturnType.FullName -eq 'System.Void') { return 0 }
            return 1
        }
        default { return -1 }
    }
}

function Get-HandlerStarts($method) {
    $set = New-Object System.Collections.Generic.HashSet[int]
    if ($method.Body.HasExceptionHandlers) {
        foreach ($h in $method.Body.ExceptionHandlers) {
            # The runtime pushes the exception onto an empty stack before the handler's first
            # instruction, so that slot has no pushing instruction of its own.
            if ($null -ne $h.HandlerStart) { [void]$set.Add($h.HandlerStart.Offset) }
        }
    }
    # The comma is load-bearing: PowerShell unrolls a collection on return, and a one-element set
    # would come back as a bare Int32.
    return ,$set
}

<#
    Maps each argument position of a target call to the instruction that pushed it.

    This is a deliberately conservative walk, not a real dataflow analysis, and the reason it is
    sound is worth stating because the obvious "smarter" version is wrong.

    A first attempt cleared the stack at every branch target, on the theory that CIL requires an
    empty stack there. It does not -- it requires only that every path agree on the depth. The
    ternary in Patches.Patch leaves its result on the stack across the branch, so clearing at the
    merge point under-flowed and the check reported that it could not follow a method it could.

    So the walk never clears. It follows the instructions in order, which means it also walks the
    untaken side of every branch and ends up with extra slots left below. That is harmless here for
    one specific reason: the arguments of a call are the *top* n slots, and junk accumulates
    underneath them. Over-counting the depth cannot change which instructions those top n are.

    Under-counting would matter, so nothing here is allowed to cause it: path terminators pop
    nothing, an under-flowing pop clamps to empty rather than going negative, and a handler's
    entry pushes a placeholder for the exception object. If the target call still finds fewer slots
    than it has arguments, or an opcode's stack effect is unrecognised, this returns $null and the
    caller reports a failure. Fail closed.
#>
function Get-ArgumentPushers($method, $target) {
    $handlerStarts = Get-HandlerStarts $method
    $stack = New-Object System.Collections.Generic.List[object]

    foreach ($ins in $method.Body.Instructions) {
        if ($handlerStarts.Contains($ins.Offset)) { $stack.Add($ins) }

        if ([object]::ReferenceEquals($ins, $target)) {
            $m = $ins.Operand
            $n = $m.Parameters.Count
            if ($m.HasThis) { $n++ }
            if ($stack.Count -lt $n) { return $null }

            $pushers = @()
            for ($i = $stack.Count - $n; $i -lt $stack.Count; $i++) { $pushers += $stack[$i] }
            # Drop the 'this' slot so index 0 is the first declared parameter.
            if ($m.HasThis) { $pushers = $pushers[1..($pushers.Count - 1)] }
            return ,$pushers
        }

        $pop = Get-PopCount $ins
        $push = Get-PushCount $ins

        # ret / leave / endfinally end a path. Popping nothing keeps the walk from under-flowing the
        # slots that the next reachable instruction may legitimately still expect.
        if ($pop -eq -1) { continue }
        if ($pop -lt 0 -or $push -lt 0) { return $null }

        if ($pop -gt $stack.Count) { $pop = $stack.Count }
        for ($i = 0; $i -lt $pop; $i++) { $stack.RemoveAt($stack.Count - 1) }
        for ($i = 0; $i -lt $push; $i++) { $stack.Add($ins) }
    }
    return $null
}

foreach ($site in $patchCallSites) {
    if ($site.Key -eq 'Harmony::PatchAll') {
        Add-Finding 5 $site.Label "calls Harmony.PatchAll; patches must go one at a time through $PatchHelper"
        continue
    }

    if ($site.Label -ne $PatchHelper) {
        Add-Finding 5 $site.Label "calls Harmony.Patch directly; every patch must go through $PatchHelper"
        continue
    }

    $pushers = Get-ArgumentPushers $site.Method $site.Instruction
    if ($null -eq $pushers) {
        Add-Finding 5 $site.Label 'Harmony.Patch arguments could not be followed, so postfix-only cannot be proven here'
        continue
    }

    # Harmony.Patch(original, prefix, postfix, transpiler, finalizer, ...). Everything but the
    # postfix must be a literal null.
    $names = @('original', 'prefix', 'postfix', 'transpiler', 'finalizer', 'ilmanipulator')
    for ($i = 1; $i -lt $pushers.Count; $i++) {
        if ($i -eq 2) { continue }   # the postfix is the one argument allowed to be non-null
        $pusher = $pushers[$i]
        if ($pusher.OpCode.Code.ToString() -ne 'Ldnull') {
            $name = if ($i -lt $names.Count) { $names[$i] } else { "argument $i" }
            Add-Finding 5 $site.Label "passes a non-null $name to Harmony.Patch (pushed by $($pusher.OpCode.Code))"
        }
    }
}

if ($patchCallSites.Count -eq 0) {
    Add-Finding 5 $PatchHelper 'no Harmony.Patch call was found at all; has the helper been renamed?'
}

# ---- Rule 6: no game type in a public signature, and none inherited -------------------------------
#
# Narrower than "no game, Unity or Lua type": AS2Ui takes and returns Rect, Color and GUIStyle, and
# AS2Events.LuaStateCreated carries a LuaInterface.Lua by design. What must never appear is a type
# from the game's own assembly, because those are the mutable objects a subscriber could write back
# through. AS2GameEvents.TrafficCollected carries a UnityEngine.Vector3 and passes: it is a value
# type, so a subscriber gets a copy.

function Test-PublicType($type) {
    $t = $type
    while ($null -ne $t) {
        if ($t.IsNested) { if (-not $t.IsNestedPublic) { return $false } }
        elseif (-not $t.IsPublic) { return $false }
        $t = $t.DeclaringType
    }
    return $true
}

function Test-SignatureMentionsGame($typeRef) {
    if ($null -eq $typeRef) { return $false }
    if ($typeRef -is [Mono.Cecil.GenericInstanceType]) {
        if (Test-IsGameType $typeRef) { return $true }
        foreach ($a in $typeRef.GenericArguments) {
            if (Test-SignatureMentionsGame $a) { return $true }
        }
        return $false
    }
    return (Test-IsGameType $typeRef)
}

foreach ($type in $allTypes) {
    if (-not (Test-PublicType $type)) { continue }

    # Inheritance is exposure, and it is the one shape the member walk below cannot see. A public
    # type deriving from a game type, or implementing a game-defined interface, hands a subscriber
    # a live game object through the base -- every inherited member arrives with it, so no field,
    # property or parameter declared here has to mention the game for the leak to happen.
    if (Test-SignatureMentionsGame $type.BaseType) {
        Add-Finding 6 $type.FullName "derives from the game type $($type.BaseType.FullName)"
    }

    foreach ($i in $type.Interfaces) {
        if (Test-SignatureMentionsGame $i.InterfaceType) {
            Add-Finding 6 $type.FullName "implements the game interface $($i.InterfaceType.FullName)"
        }
    }

    foreach ($f in $type.Fields) {
        if (-not ($f.IsPublic)) { continue }
        if (Test-SignatureMentionsGame $f.FieldType) {
            Add-Finding 6 "$($type.FullName)::$($f.Name)" "public field exposes the game type $($f.FieldType.FullName)"
        }
    }

    foreach ($e in $type.Events) {
        if (Test-SignatureMentionsGame $e.EventType) {
            $addM = $e.AddMethod
            if ($null -ne $addM -and $addM.IsPublic) {
                Add-Finding 6 "$($type.FullName)::$($e.Name)" "public event exposes the game type $($e.EventType.FullName)"
            }
        }
    }

    foreach ($p in $type.Properties) {
        $getter = $p.GetMethod
        $isPublic = ($null -ne $getter -and $getter.IsPublic)
        if ($isPublic -and (Test-SignatureMentionsGame $p.PropertyType)) {
            Add-Finding 6 "$($type.FullName)::$($p.Name)" "public property exposes the game type $($p.PropertyType.FullName)"
        }
    }

    foreach ($m in $type.Methods) {
        if (-not $m.IsPublic) { continue }
        if ($m.IsGetter -or $m.IsSetter -or $m.IsAddOn -or $m.IsRemoveOn) { continue }
        if (Test-SignatureMentionsGame $m.ReturnType) {
            Add-Finding 6 (Get-MethodLabel $m) "returns the game type $($m.ReturnType.FullName)"
        }
        foreach ($p in $m.Parameters) {
            if (Test-SignatureMentionsGame $p.ParameterType) {
                Add-Finding 6 (Get-MethodLabel $m) "takes the game type $($p.ParameterType.FullName) as '$($p.Name)'"
            }
        }
    }
}

# ---- Report ---------------------------------------------------------------------------------------

$module.Dispose()

if ($findings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($findings.Count) invariant violation(s) in $Assembly" -ForegroundColor Red
    foreach ($f in $findings) { Write-Host $f -ForegroundColor Red }
    Write-Host ''
    Write-Host 'These are the claims README.md makes to users. Fix the code, or change the claim and' -ForegroundColor Yellow
    Write-Host 'the allowlist in this script together -- deliberately, with a comment saying why.' -ForegroundColor Yellow
    exit 1
}

Write-Host '    rule 1  no Broadcast on the game bus' -ForegroundColor Green
Write-Host '    rule 2  no game field written' -ForegroundColor Green
Write-Host '    rule 3  no reflective setter called' -ForegroundColor Green
Write-Host '    rule 4  every call into the game is on the reviewed allowlist' -ForegroundColor Green
Write-Host '    rule 5  Harmony patches are postfix-only and go through one helper' -ForegroundColor Green
Write-Host '    rule 6  no public member exposes a game type, and no public type inherits one' -ForegroundColor Green
Write-Host 'All invariants hold.' -ForegroundColor Green
exit 0
