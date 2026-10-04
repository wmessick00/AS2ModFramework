<#
.SYNOPSIS
    Cold checks for the two release scripts that read an assembly with Mono.Cecil.

.DESCRIPTION
    tools\verify-invariants.ps1 proves, from the built AS2.ModApi.dll, that the framework only
    observes the game, and tools\pack.ps1 will not archive anything it fails. tools\Get-NextVersion.ps1
    compares the public surface of the assembly about to ship with the last release, and picks the
    segment of the version from the difference. Both run against a DLL that cannot be built on a
    runner, so nothing exercised either of them: tests\Check-ReleaseTooling.ps1 holds the arithmetic
    and the path globs and stops there.

    These checks do not need the real DLL. Mono.Cecil can write an assembly as readily as it reads
    one, so each check builds the few types and calls it is about, and runs the real script on the
    result. A fixture is a handful of lines, and the claim under test is the one the script makes.

    Mono.Cecil is not a dependency of this repository, and this does not add one. It ships inside
    BepInEx, which tools\pack.ps1 already pins by version and hash, so that is where it comes from:
    this reads the pin out of pack.ps1 rather than keeping a second copy of it, downloads the same
    zip, refuses it on a hash mismatch, and takes the one DLL it needs. Pass -CecilPath, or set
    AS2_CECIL, to use one you already have.

    No test framework, for the reason tests\Check-ReleaseTooling.ps1 gives.

.PARAMETER CecilPath
    Mono.Cecil.dll. Defaults to AS2_CECIL, then the pack staging area, then the pinned download.

.EXAMPLE
    .\tests\Check-Assemblies.ps1
#>

[CmdletBinding()]
param(
    [string]$CecilPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failures = New-Object System.Collections.Generic.List[string]

function Pass($what) {
    $script:Passed++
    Write-Output "  PASS  $what"
}

function Fail($what) {
    $script:Failures.Add($what)
    Write-Output "  FAIL  $what"
}

function Same($what, $actual, $expected) {
    if ($actual -eq $expected) { Pass $what }
    else { Fail "$what -- expected '$expected', got '$actual'" }
}

function True($what, $actual) {
    if ($actual) { Pass $what } else { Fail "$what -- expected true" }
}

function False($what, $actual) {
    if (-not $actual) { Pass $what } else { Fail "$what -- expected false" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot

# ---- Mono.Cecil ------------------------------------------------------------------------------------
#
# The pin lives in tools\pack.ps1 and is read from there, so a version bump there is a version bump
# here with nothing to remember. If the pin is moved or reworded, this stops and says so rather than
# falling back to something unpinned.

function Get-PinnedCecil {
    $pack = Get-Content -Raw -Path (Join-Path $repoRoot 'tools\pack.ps1')

    $version = [regex]::Match($pack, '\$BepInExVersion\s*=\s*''([^'']+)''')
    $url     = [regex]::Match($pack, '\$BepInExUrl\s*=\s*"([^"]+)"')
    $hash    = [regex]::Match($pack, '\$BepInExSha256\s*=\s*''([0-9A-Fa-f]{64})''')

    if (-not ($version.Success -and $url.Success -and $hash.Success)) {
        throw 'tools\pack.ps1 no longer defines $BepInExVersion, $BepInExUrl and $BepInExSha256 in the form this reads. If they moved, change where this looks.'
    }

    $v = $version.Groups[1].Value
    $link = $url.Groups[1].Value.Replace('$BepInExVersion', $v)

    $cache = Join-Path ([IO.Path]::GetTempPath()) "as2-cecil-$v"
    $dll = Join-Path $cache 'Mono.Cecil.dll'
    if (Test-Path $dll) { return $dll }

    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $zip = Join-Path $cache "BepInEx_win_x64_$v.zip"

    # Windows PowerShell 5.1 does not offer TLS 1.2 to a server by default.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-WebRequest -Uri $link -OutFile $zip -UseBasicParsing

    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $hash.Groups[1].Value.ToUpperInvariant()) {
        Remove-Item $zip -Force
        throw "The BepInEx zip does not match the hash pinned in tools\pack.ps1.`n  expected $($hash.Groups[1].Value)`n  actual   $actual"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq 'BepInEx/core/Mono.Cecil.dll' } | Select-Object -First 1
        if ($null -eq $entry) { throw 'The pinned BepInEx zip holds no BepInEx/core/Mono.Cecil.dll.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll, $true)
    }
    finally { $archive.Dispose() }

    return $dll
}

function Find-Cecil {
    if ($CecilPath) {
        if (-not (Test-Path $CecilPath)) { throw "Mono.Cecil.dll not found at -CecilPath: $CecilPath" }
        return $CecilPath
    }
    if ($env:AS2_CECIL) {
        if (-not (Test-Path $env:AS2_CECIL)) { throw "Mono.Cecil.dll not found at AS2_CECIL: $env:AS2_CECIL" }
        return $env:AS2_CECIL
    }
    $staged = Join-Path $repoRoot 'build\stage\BepInEx\core\Mono.Cecil.dll'
    if (Test-Path $staged) { return $staged }

    return Get-PinnedCecil
}

$cecil = Find-Cecil
Add-Type -Path $cecil | Out-Null
Write-Output "Using $cecil"
Write-Output ''

# ---- Writing a fixture assembly --------------------------------------------------------------------
#
# Nothing here is ever run. The scripts under test read IL, so a method body is a few call
# instructions and the return of the right type, and a type reference into the game is a scope
# naming Assembly-CSharp, which is all the scripts look at to call a type the game's.

$work = Join-Path ([IO.Path]::GetTempPath()) ("as2-assemblies-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null

function New-Fixture($name) {
    $asm = [Mono.Cecil.AssemblyDefinition]::CreateAssembly(
        (New-Object Mono.Cecil.AssemblyNameDefinition($name, ([version]'1.0.0.0'))), $name, [Mono.Cecil.ModuleKind]::Dll)
    return $asm
}

function Add-PublicClass($asm, $namespace, $name) {
    $module = $asm.MainModule
    $attrs = [Mono.Cecil.TypeAttributes]::Public -bor [Mono.Cecil.TypeAttributes]::Abstract -bor [Mono.Cecil.TypeAttributes]::Sealed
    $t = New-Object Mono.Cecil.TypeDefinition($namespace, $name, $attrs, $module.TypeSystem.Object)
    $module.Types.Add($t)
    return $t
}

# A static method on the type whose body makes the given calls, in order, and discards each result.
function Add-CallingMethod($type, $name, $callees) {
    $module = $type.Module
    $attrs = [Mono.Cecil.MethodAttributes]::Public -bor [Mono.Cecil.MethodAttributes]::Static
    $m = New-Object Mono.Cecil.MethodDefinition($name, $attrs, $module.TypeSystem.Void)
    $il = $m.Body.GetILProcessor()
    foreach ($callee in $callees) {
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $callee))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Pop))
    }
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $type.Methods.Add($m)
    return $m
}

# A reference to a static method on a type of the game's own assembly. $outer is the name of a type
# it is nested in, for the nested case, and $namespace is the game type's namespace.
function New-GameCall($module, $namespace, $typeName, $method, $outer = '', $generic = $false) {
    $scope = $null
    foreach ($r in $module.AssemblyReferences) { if ($r.Name -eq 'Assembly-CSharp') { $scope = $r } }
    if ($null -eq $scope) {
        $scope = New-Object Mono.Cecil.AssemblyNameReference('Assembly-CSharp', ([version]'0.0.0.0'))
        $module.AssemblyReferences.Add($scope)
    }

    $type = New-Object Mono.Cecil.TypeReference($namespace, $typeName, $module, $scope)
    if ($outer) {
        $enclosing = New-Object Mono.Cecil.TypeReference('', $outer, $module, $scope)
        $type.DeclaringType = $enclosing
    }

    $declaring = $type
    if ($generic) {
        $declaring = New-Object Mono.Cecil.GenericInstanceType($type)
        $declaring.GenericArguments.Add($module.TypeSystem.String)
    }

    $ref = New-Object Mono.Cecil.MethodReference($method, $module.TypeSystem.String, $declaring)
    $ref.HasThis = $false
    return $ref
}


# ---- Describing a public surface -------------------------------------------------------------------
#
# Just enough Cecil to say "a public class with these members", for the surface comparison. A type
# from the core library is written as a reference rather than imported from the running PowerShell,
# whose own runtime is not the one the game has, and the reference is all the scripts read.

function New-CoreType($module, $namespace, $name) {
    return New-Object Mono.Cecil.TypeReference($namespace, $name, $module, $module.TypeSystem.CoreLibrary)
}

function Add-Class($asm, $name, [string]$visibility = 'public', $base = $null, [string[]]$interfaces = @(), $namespace = 'Fixture') {
    $module = $asm.MainModule
    $attrs = switch ($visibility) { 'public' { [Mono.Cecil.TypeAttributes]::Public } default { [Mono.Cecil.TypeAttributes]::NotPublic } }
    if ($null -eq $base) { $base = $module.TypeSystem.Object }
    $t = New-Object Mono.Cecil.TypeDefinition($namespace, $name, $attrs, $base)
    foreach ($i in $interfaces) {
        $t.Interfaces.Add((New-Object Mono.Cecil.InterfaceImplementation((New-CoreType $module 'System' $i))))
    }
    $module.Types.Add($t)
    return $t
}

function Add-Nested($outer, $name, [string]$visibility = 'public') {
    $module = $outer.Module
    $attrs = switch ($visibility) {
        'public'  { [Mono.Cecil.TypeAttributes]::NestedPublic }
        'family'  { [Mono.Cecil.TypeAttributes]::NestedFamily }
        default   { [Mono.Cecil.TypeAttributes]::NestedPrivate }
    }
    $t = New-Object Mono.Cecil.TypeDefinition('', $name, $attrs, $module.TypeSystem.Object)
    $outer.NestedTypes.Add($t)
    return $t
}

function Add-Method($type, $name, [string]$visibility = 'public', [switch]$static, $params = @(), $return = $null, [int]$generics = 0) {
    $module = $type.Module
    $attrs = switch ($visibility) {
        'public'   { [Mono.Cecil.MethodAttributes]::Public }
        'family'   { [Mono.Cecil.MethodAttributes]::Family }
        'internal' { [Mono.Cecil.MethodAttributes]::Assembly }
        default    { [Mono.Cecil.MethodAttributes]::Private }
    }
    if ($static) { $attrs = $attrs -bor [Mono.Cecil.MethodAttributes]::Static }
    if ($null -eq $return) { $return = $module.TypeSystem.Void }

    $m = New-Object Mono.Cecil.MethodDefinition($name, $attrs, $return)
    $n = 0
    foreach ($p in $params) {
        $m.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition(("p" + $n++), [Mono.Cecil.ParameterAttributes]::None, $p)))
    }
    for ($g = 0; $g -lt $generics; $g++) {
        $m.GenericParameters.Add((New-Object Mono.Cecil.GenericParameter(("T" + $g), $m)))
    }
    $il = $m.Body.GetILProcessor()
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $type.Methods.Add($m)
    return $m
}

function Add-Field($type, $name, $fieldType, [string]$visibility = 'public', [switch]$static) {
    $attrs = switch ($visibility) {
        'public' { [Mono.Cecil.FieldAttributes]::Public }
        'family' { [Mono.Cecil.FieldAttributes]::Family }
        default  { [Mono.Cecil.FieldAttributes]::Private }
    }
    if ($static) { $attrs = $attrs -bor [Mono.Cecil.FieldAttributes]::Static }
    $f = New-Object Mono.Cecil.FieldDefinition($name, $attrs, $fieldType)
    $type.Fields.Add($f)
    return $f
}

# A public const string: a literal static field with a value. Its value is exactly what the surface
# must not render, since the version constant is one and changes in every release.
function Add-Constant($type, $name, $value) {
    $attrs = [Mono.Cecil.FieldAttributes]::Public -bor [Mono.Cecil.FieldAttributes]::Static -bor
             [Mono.Cecil.FieldAttributes]::Literal -bor [Mono.Cecil.FieldAttributes]::HasDefault
    $f = New-Object Mono.Cecil.FieldDefinition($name, $attrs, $type.Module.TypeSystem.String)
    $f.Constant = $value
    $type.Fields.Add($f)
    return $f
}

# An enum: System.Enum as its base, the value__ field every enum has, and one literal per member.
function Add-Enum($asm, $name, $members, [string]$visibility = 'public') {
    $module = $asm.MainModule
    $attrs = [Mono.Cecil.TypeAttributes]::Sealed -bor
             $(if ($visibility -eq 'public') { [Mono.Cecil.TypeAttributes]::Public } else { [Mono.Cecil.TypeAttributes]::NotPublic })
    $t = New-Object Mono.Cecil.TypeDefinition('Fixture', $name, $attrs, (New-CoreType $module 'System' 'Enum'))

    $special = [Mono.Cecil.FieldAttributes]::Public -bor [Mono.Cecil.FieldAttributes]::SpecialName -bor [Mono.Cecil.FieldAttributes]::RTSpecialName
    $t.Fields.Add((New-Object Mono.Cecil.FieldDefinition('value__', $special, $module.TypeSystem.Int32)))

    foreach ($pair in $members) {
        $flags = [Mono.Cecil.FieldAttributes]::Public -bor [Mono.Cecil.FieldAttributes]::Static -bor
                 [Mono.Cecil.FieldAttributes]::Literal -bor [Mono.Cecil.FieldAttributes]::HasDefault
        $f = New-Object Mono.Cecil.FieldDefinition($pair[0], $flags, $t)
        $f.Constant = [int]$pair[1]
        $t.Fields.Add($f)
    }

    $module.Types.Add($t)
    return $t
}

function Save-Fixture($asm, $fileName) {
    $path = Join-Path $work $fileName
    $asm.Write($path)
    $asm.Dispose()
    return $path
}

# Runs a script in a process of its own and returns what it printed and how it ended. These scripts
# call exit, which would end this one.
function Invoke-Script($scriptPath, [string[]]$arguments) {
    # The same PowerShell that is running this, so a maintainer's Windows PowerShell is what runs
    # the script under test, as it does at a release. PowerShell installed as a dotnet tool runs as
    # "dotnet pwsh.dll", and then the host to start is dotnet with the dll in front.
    $exe = (Get-Process -Id $PID).Path
    $lead = @()
    if ([IO.Path]::GetFileNameWithoutExtension($exe) -eq 'dotnet') { $lead = @((Join-Path $PSHOME 'pwsh.dll')) }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& $exe @lead -NoProfile -File $scriptPath @arguments 2>&1 | Out-String)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }

    return [pscustomobject]@{ Text = $text; ExitCode = $code }
}

try {

# ---- verify-invariants.ps1, rule 4: the allowlist matches the whole type name --------------------
#
# Issue #85. A call into the game has to be on a reviewed list, and the list was matched on the
# type's simple name. A second type with the same simple name in another namespace, or nested in
# another type, borrowed the review of the one that was actually looked at.

Write-Output 'verify-invariants.ps1, rule 4: the allowlist is matched on the whole type name'

$verify = Join-Path $repoRoot 'tools\verify-invariants.ps1'

$asm = New-Fixture 'Fixture.Rule4'
$calls = Add-PublicClass $asm 'Fixture' 'Calls'
$module = $asm.MainModule

[void](Add-CallingMethod $calls 'Run' @(
    # On the allowlist exactly as reviewed: global namespace, not nested.
    (New-GameCall $module '' 'UIManager' 'Exists'),
    (New-GameCall $module '' 'Mode' 'get_relativePath'),
    # A generic type, which is matched by its definition and not by its instantiation.
    (New-GameCall $module '' 'Messenger`1' 'AddListener' '' $true),

    # The same simple names and the same members, and nobody has looked at any of them.
    (New-GameCall $module 'Other.Namespace' 'Mode' 'get_relativePath'),
    (New-GameCall $module '' 'Mode' 'get_relativePath' 'SomeOuterType'),
    (New-GameCall $module 'Another' 'UIManager' 'Exists'),

    # And a call that is on nobody's list, so the rule is shown still to refuse what it always did.
    (New-GameCall $module '' 'UIManager' 'Destroy')
))

$dll = Save-Fixture $asm 'rule4.dll'
$result = Invoke-Script $verify @('-Assembly', $dll, '-CecilPath', $cecil)

$flagged = @()
foreach ($m in [regex]::Matches($result.Text, 'calls (\S+), which is not on the reviewed allowlist')) {
    $flagged += $m.Groups[1].Value
}

True 'the script reports violations, and says so with a failing exit code' ($result.ExitCode -eq 1)

False 'a call on the allowlist, as reviewed, is accepted'              ($flagged -contains 'UIManager::Exists')
False 'a second call on the allowlist, as reviewed, is accepted'       ($flagged -contains 'Mode::get_relativePath')
False 'a generic type is matched by its definition, as before'         ($flagged -contains 'Messenger`1::AddListener')

True 'a type of the same name in another namespace is not the reviewed one' `
     ($flagged -contains 'Other.Namespace.Mode::get_relativePath')
True 'a type of the same name nested in another type is not the reviewed one' `
     ($flagged -contains 'SomeOuterType/Mode::get_relativePath')
True 'a second UIManager in another namespace is not the reviewed one' `
     ($flagged -contains 'Another.UIManager::Exists')
True 'a call that was never on the list is still refused' ($flagged -contains 'UIManager::Destroy')
Same 'and nothing else was flagged by rule 4' $flagged.Count 4

# The message is what a maintainer copies into the allowlist, so it has to be the key as written.
True 'the message names the full type, so that an entry for it is a copy and paste' `
     ($result.Text -match 'calls Other\.Namespace\.Mode::get_relativePath, which is not on the reviewed allowlist')


# ---- Get-NextVersion.ps1, rule 1: the public surface decides major or minor ----------------------
#
# Issue #83. "The whole contract for a library like AS2.ModApi", in the script's own words, and
# nothing held it: the checks for this script cover the arithmetic and the path globs. A subtle
# fault here ships a breaking change as a minor or a patch, and every mod compiled against the
# framework meets it as a missing method.
#
# Each case is two assemblies, a tagged repository to stand for the last release, and the real
# script. The verdict is what the script says; 'none' is what falls out when the surface is
# unchanged and no file changed since the tag, so it stands for "rule 1 found nothing".

Write-Output ''
Write-Output 'Get-NextVersion.ps1, rule 1: what the public surface says'

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) { throw 'git is needed for these checks, to stand in for a tagged release.' }

$repo = Join-Path $work 'repo'
New-Item -ItemType Directory -Force -Path $repo | Out-Null
foreach ($cmd in @(
        @('init', '-q'),
        @('-c', 'user.email=check@example.invalid', '-c', 'user.name=check', 'commit', '-q', '--allow-empty', '-m', 'release'),
        @('tag', 'v1.0.0'))) {
    & git -C $repo @cmd 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($cmd -join ' ') failed in the fixture repository" }
}

$nextVersion = Join-Path $repoRoot 'tools\Get-NextVersion.ps1'
$caseNumber = 0

# $before and $after each build one assembly. The function returns the script's verdict object.
function Get-Verdict([scriptblock]$before, [scriptblock]$after) {
    $script:caseNumber++
    $old = New-Fixture "Fixture.Before$($script:caseNumber)"
    & $before $old
    $oldPath = Save-Fixture $old "before$($script:caseNumber).dll"

    $new = New-Fixture "Fixture.After$($script:caseNumber)"
    & $after $new
    $newPath = Save-Fixture $new "after$($script:caseNumber).dll"

    return & $nextVersion -RepoRoot $repo -Assembly $newPath -PreviousAssembly $oldPath `
                          -CurrentVersion '1.0.0' -CecilPath $cecil
}

function Check-Level($what, $expected, [scriptblock]$before, [scriptblock]$after) {
    $v = Get-Verdict $before $after
    Same $what $v.Level $expected
}

# What each type in a case refers to, written once.
function Str($asm) { return $asm.MainModule.TypeSystem.String }
function Int($asm) { return $asm.MainModule.TypeSystem.Int32 }

$onePublicMethod = { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -params @(Str $a) -return (Int $a)) }

Check-Level 'an identical surface says nothing about the version' 'none' $onePublicMethod $onePublicMethod

# ---- Methods

Check-Level 'a public method removed is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run'); [void](Add-Method $t 'Stop') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run') }

Check-Level 'a public method added alone is a minor' 'minor' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run'); [void](Add-Method $t 'Stop') }

Check-Level 'a changed parameter type is a major, being a removal and an addition' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -params @(Str $a)) } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -params @(Int $a)) }

Check-Level 'a changed return type is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -return (Str $a)) } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -return (Int $a)) }

Check-Level 'an instance method made static is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -static) }

Check-Level 'a generic method gaining a type parameter is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -generics 1) } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -generics 2) }

# ---- What counts as surface

Check-Level 'a protected method removed is a major: a subclass binds to it' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Hook' 'family') } `
    { param($a) $t = Add-Class $a 'Api' }

Check-Level 'a private method removed is not surface' 'none' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run'); [void](Add-Method $t 'Helper' 'private') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run') }

Check-Level 'an internal method removed is not surface' 'none' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run'); [void](Add-Method $t 'Helper' 'internal') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run') }

Check-Level 'an internal type removed is not surface' 'none' `
    { param($a) [void](Add-Class $a 'Api'); [void](Add-Class $a 'Inner' 'internal') } `
    { param($a) [void](Add-Class $a 'Api') }

Check-Level 'a public type removed is a major' 'major' `
    { param($a) [void](Add-Class $a 'Api'); [void](Add-Class $a 'Other') } `
    { param($a) [void](Add-Class $a 'Api') }

Check-Level 'a public type added alone is a minor' 'minor' `
    { param($a) [void](Add-Class $a 'Api') } `
    { param($a) [void](Add-Class $a 'Api'); [void](Add-Class $a 'Other') }

# ---- Fields

Check-Level 'a public field removed is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Field $t 'Count' (Int $a)) } `
    { param($a) [void](Add-Class $a 'Api') }

Check-Level 'a changed field type is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Field $t 'Count' (Int $a)) } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Field $t 'Count' (Str $a)) }

Check-Level 'a private field removed is not surface' 'none' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Field $t 'hidden' (Int $a) 'private') } `
    { param($a) [void](Add-Class $a 'Api') }

# The version constant is a public field whose value changes in every release. Rendering values for
# ordinary fields would make every release a surface change and rule 1 would report a minor forever.
Check-Level "a public const's value changing is not a change of surface" 'none' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Constant $t 'Version' '1.0.0') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Constant $t 'Version' '1.0.1') }

# ---- Enums

Check-Level 'an enum renumbered with every name unchanged is a major' 'major' `
    { param($a) [void](Add-Enum $a 'Kind' @(@('Skin', 0), @('Mode', 1))) } `
    { param($a) [void](Add-Enum $a 'Kind' @(@('Skin', 1), @('Mode', 0))) }

Check-Level 'a member added to an enum, the others unchanged, is a minor' 'minor' `
    { param($a) [void](Add-Enum $a 'Kind' @(@('Skin', 0), @('Mode', 1))) } `
    { param($a) [void](Add-Enum $a 'Kind' @(@('Skin', 0), @('Mode', 1), @('Both', 2))) }

Check-Level 'an enum member removed is a major' 'major' `
    { param($a) [void](Add-Enum $a 'Kind' @(@('Skin', 0), @('Mode', 1))) } `
    { param($a) [void](Add-Enum $a 'Kind' @(,@('Skin', 0))) }

Check-Level 'an enum that is not public is not surface' 'none' `
    { param($a) [void](Add-Enum $a 'Kind' @(,@('Skin', 0)) 'internal') } `
    { param($a) [void](Add-Enum $a 'Kind' @(,@('Skin', 5)) 'internal') }

# ---- Nested types: reachable only if everything around them is

Check-Level 'a public nested type removed is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Nested $t 'Options') } `
    { param($a) [void](Add-Class $a 'Api') }

Check-Level 'a protected nested type removed is a major' 'major' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Nested $t 'Hooks' 'family') } `
    { param($a) [void](Add-Class $a 'Api') }

Check-Level 'a private nested type removed is not surface' 'none' `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Nested $t 'Cache' 'private') } `
    { param($a) [void](Add-Class $a 'Api') }

Check-Level 'a public nested type inside an internal type is not surface' 'none' `
    { param($a) $t = Add-Class $a 'Inner' 'internal'; $n = Add-Nested $t 'Options'; [void](Add-Method $n 'Run') } `
    { param($a) [void](Add-Class $a 'Inner' 'internal') }

# ---- What a type derives from and implements

Check-Level 'a type that stops implementing an interface is a major' 'major' `
    { param($a) [void](Add-Class $a 'Api' 'public' $null @('IDisposable')) } `
    { param($a) [void](Add-Class $a 'Api') }

# Over-cautious rather than exact, and pinned as it is. The interfaces are part of the type's own line
# in the surface, so a type that gains one has a different line from the one it had, which reads as
# the old line removed and a new one added. An addition would be a minor in strict semver; this says
# major. A release that is numbered too high costs nothing, and the other way round is the failure
# this rule exists for, so a change to this should be deliberate.
Check-Level 'a type that starts implementing an interface reads as a major, the type line having changed' 'major' `
    { param($a) [void](Add-Class $a 'Api') } `
    { param($a) [void](Add-Class $a 'Api' 'public' $null @('IDisposable')) }

Check-Level 'the interfaces listed in another order are the same surface' 'none' `
    { param($a) [void](Add-Class $a 'Api' 'public' $null @('IDisposable', 'IComparable')) } `
    { param($a) [void](Add-Class $a 'Api' 'public' $null @('IComparable', 'IDisposable')) }

Check-Level 'a changed base type is a major' 'major' `
    { param($a) [void](Add-Class $a 'Api') } `
    { param($a) [void](Add-Class $a 'Api' 'public' (New-CoreType $a.MainModule 'System' 'Exception')) }

# ---- The verdict carries its evidence

$removal = Get-Verdict `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run'); [void](Add-Method $t 'Stop') } `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run') }

True 'a major names the member that went, with a minus' `
     (@(@($removal.Reasons) | Where-Object { $_ -match '^\s*- .*Api::Stop' }).Count -eq 1)
True 'and says how the surface changed in numbers' `
     (@(@($removal.Reasons) | Where-Object { $_ -match 'public surface .* 1 removed' }).Count -ge 1)
Same 'the next version steps the major' $removal.Next '2.0.0'

$addition = Get-Verdict $onePublicMethod `
    { param($a) $t = Add-Class $a 'Api'; [void](Add-Method $t 'Run' 'public' -params @(Str $a) -return (Int $a)); [void](Add-Method $t 'Extra') }

Same 'the next version after an addition steps the minor' $addition.Next '1.1.0'
}
finally {
    try { Remove-Item -Recurse -Force $work } catch { }
}

Write-Output ''
if ($script:Failures.Count -eq 0) {
    Write-Output "All $($script:Passed) checks passed."
    exit 0
}

Write-Output "$($script:Failures.Count) FAILED ($($script:Passed) passed):"
foreach ($f in $script:Failures) { Write-Output "  - $f" }
exit 1
