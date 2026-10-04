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
