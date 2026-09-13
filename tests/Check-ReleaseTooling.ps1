<#
.SYNOPSIS
    Cold checks for the version decision in tools\Get-NextVersion.ps1.

.DESCRIPTION
    That script decides the segment of every release this repository publishes: major or minor from
    a Mono.Cecil diff of the public surface, patch or none from matching changed paths against the
    declared globs. Nothing checked any of it. A regression in the glob rules would ship a breaking
    change as a patch release, and nothing in CI would notice -- which is the same class of fault as
    AS2-SkinSettings issue #47, in the same pipeline.

    No test framework, matching tests\Check.cs and for the same stated reason: this repository takes
    no dependency it does not already have, and a script that prints PASS lines and returns an exit
    code is understood by every CI there is. Pester 3.4.0 is what ships with Windows, its syntax is
    two major versions behind current, and installing a newer one in CI would be a dependency added
    to test a dependency-free pipeline.

    The functions are lifted out of Get-NextVersion.ps1 by parsing it, rather than dot-sourcing it.
    Dot-sourcing runs the whole decision: the script has a param block, it throws on a missing
    assembly at the top level, and it ends by returning a verdict. Parsing gets the functions
    without any of that, and without restructuring a script whose failure mode is a bad release.

.EXAMPLE
    .\tests\Check-ReleaseTooling.ps1
#>

[CmdletBinding()]
param()

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

function Throws($what, [scriptblock]$body) {
    try {
        & $body
        Fail "$what -- expected a throw, got none"
    }
    catch { Pass $what }
}

# ---- Lifting the functions out --------------------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $repoRoot 'tools\Get-NextVersion.ps1'

if (-not (Test-Path $script)) { throw "Not found: $script" }

$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
if ($errors -and $errors.Count -gt 0) {
    throw "tools\Get-NextVersion.ps1 does not parse: $($errors[0].Message)"
}

$wanted = @('ConvertTo-SemVer', 'Format-SemVer', 'Compare-SemVer', 'Step-SemVer',
            'Test-MatchesAny', 'Get-PathVerdict')

$found = @{}
foreach ($fn in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($wanted -contains $fn.Name) { $found[$fn.Name] = $fn.Extent.Text }
}

foreach ($name in $wanted) {
    if (-not $found.ContainsKey($name)) {
        throw "Get-NextVersion.ps1 no longer defines $name. If it was renamed, rename it here too."
    }
    Invoke-Expression $found[$name]
}

Write-Output "Lifted $($wanted.Count) functions out of tools\Get-NextVersion.ps1"
Write-Output ''

# ---- Semantic versions ----------------------------------------------------------------------------

Write-Output 'Reading a version'

Same 'a three-part version reads as three numbers' (Format-SemVer (ConvertTo-SemVer '1.2.3')) '1.2.3'
Same 'surrounding space is allowed' (Format-SemVer (ConvertTo-SemVer '  0.2.2  ')) '0.2.2'
Same 'a zero version is a version' (Format-SemVer (ConvertTo-SemVer '0.0.0')) '0.0.0'
Same 'and the parts are numbers, not text' (Format-SemVer (ConvertTo-SemVer '1.10.2')) '1.10.2'

# Every shape the rest of the pipeline could not name. A tag is formed as "v$version", so anything
# accepted here has to survive being a tag and being compared with the last one.
foreach ($bad in @('1.2', '1.2.3.4', 'v1.2.3', '1.2.3-rc1', '1.2.3+build', 'one.two.three', '', '1..3')) {
    Throws "'$bad' is refused" { ConvertTo-SemVer $bad }
}

Write-Output ''
Write-Output 'Comparing two versions'

True 'a later patch is greater'  ((Compare-SemVer (ConvertTo-SemVer '1.2.4') (ConvertTo-SemVer '1.2.3')) -gt 0)
True 'a later minor is greater'  ((Compare-SemVer (ConvertTo-SemVer '1.3.0') (ConvertTo-SemVer '1.2.9')) -gt 0)
True 'a later major is greater'  ((Compare-SemVer (ConvertTo-SemVer '2.0.0') (ConvertTo-SemVer '1.9.9')) -gt 0)
True 'the same version is equal' ((Compare-SemVer (ConvertTo-SemVer '1.2.3') (ConvertTo-SemVer '1.2.3')) -eq 0)
True 'an earlier version is less' ((Compare-SemVer (ConvertTo-SemVer '1.2.3') (ConvertTo-SemVer '1.2.4')) -lt 0)

# The one a string comparison gets wrong, and the reason this is not a string comparison.
True '1.10.0 is later than 1.9.0, which sorts the other way as text' `
     ((Compare-SemVer (ConvertTo-SemVer '1.10.0') (ConvertTo-SemVer '1.9.0')) -gt 0)

Write-Output ''
Write-Output 'Stepping a version'

# The zeroing is the part worth checking. A minor bump that kept the patch, or a major that kept
# the minor, publishes a number that reads as later than it is.
Same 'a major bump zeroes the two below it' (Format-SemVer (Step-SemVer (ConvertTo-SemVer '1.4.7') 'major')) '2.0.0'
Same 'a minor bump zeroes the patch'        (Format-SemVer (Step-SemVer (ConvertTo-SemVer '1.4.7') 'minor')) '1.5.0'
Same 'a patch bump touches nothing else'    (Format-SemVer (Step-SemVer (ConvertTo-SemVer '1.4.7') 'patch')) '1.4.8'
Same 'stepping from zero works'             (Format-SemVer (Step-SemVer (ConvertTo-SemVer '0.0.0') 'minor')) '0.1.0'

Throws "'none' is not a step, and has to be refused rather than treated as a patch" `
    { Step-SemVer (ConvertTo-SemVer '1.2.3') 'none' }
Throws 'nor is anything else' { Step-SemVer (ConvertTo-SemVer '1.2.3') 'build' }
Throws 'nor is nothing at all' { Step-SemVer (ConvertTo-SemVer '1.2.3') '' }

# Found by this check asserting the opposite first. PowerShell's switch is case-insensitive
# unless it is told otherwise, so 'Major' is accepted as readily as 'major'. Every caller
# passes a lowercase literal, so nothing turns on it -- but it is behaviour rather than an
# accident now that it is written down, and a later -casesensitive would be a change.
Same 'a level is matched without regard to case' `
     (Format-SemVer (Step-SemVer (ConvertTo-SemVer '1.4.7') 'MAJOR')) '2.0.0'

# ---- The path globs -------------------------------------------------------------------------------

Write-Output ''
Write-Output 'Matching a changed path'

# git reports forward slashes, and PowerShell's -like lets an asterisk cross a separator -- which is
# why the globs need no ** form. That is a property of -like rather than of this code, so it is
# worth pinning: if it stopped holding, docs/* would silently stop matching anything nested.
True 'an asterisk crosses a separator'     (Test-MatchesAny 'docs/reference/probe-dump.txt' @('docs/*'))
True 'a plain prefix matches'              (Test-MatchesAny 'docs/environment.md' @('docs/*'))
True 'the first of several patterns wins'  (Test-MatchesAny 'src/AS2.ModApi/Str.cs' @('docs/*', 'src/*'))
True 'an exact path matches itself'        (Test-MatchesAny 'README.md' @('README.md'))

True 'a path matching nothing is false'    (-not (Test-MatchesAny 'src/AS2.ModApi/Str.cs' @('docs/*')))
True 'an empty pattern list matches nothing' (-not (Test-MatchesAny 'anything.txt' @()))

# Backslashes are what a Windows-shaped glob would be written with, and git never reports one. A
# pattern written that way silently matches nothing, which reads as "nothing changed".
True 'a backslash pattern does not match what git reports' `
     (-not (Test-MatchesAny 'docs/environment.md' @('docs\*')))

# ---- The verdict the globs produce ------------------------------------------------------------------

Write-Output ''
Write-Output 'Deciding from what changed'

# Get-PathVerdict reads three things out of its enclosing scope: the two glob lists, the repo root,
# and the reasons list it appends to. It calls git through Invoke-Native. Both are supplied here, so
# the decision is exercised without a repository and without a network.
$RepoRoot = $repoRoot
$MinorPaths = @('docs/*', 'README.md')
$NoBumpPaths = @('tests/*', '.github/*')
$reasons = New-Object System.Collections.Generic.List[string]

$script:FakeChanged = @()
$script:FakeExitCode = 0

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    return [pscustomobject]@{ ExitCode = $script:FakeExitCode; Output = $script:FakeChanged; Error = @() }
}

function Verdict($changed, $exitCode = 0) {
    $script:FakeChanged = $changed
    $script:FakeExitCode = $exitCode
    $reasons.Clear()
    return Get-PathVerdict 'v1.0.0'
}

Same 'nothing changed is no bump at all'          (Verdict @()) 'none'
Same 'a source file alone is a patch'             (Verdict @('src/AS2.ModApi/Str.cs')) 'patch'
Same 'a declared contract path is a minor'        (Verdict @('docs/environment.md')) 'minor'
Same 'a no-bump path alone is none'               (Verdict @('tests/Check.cs')) 'none'

# The precedence, which is the part a regression would quietly invert.
Same 'a minor path outranks a patch one'          (Verdict @('src/AS2.ModApi/Str.cs', 'docs/environment.md')) 'minor'
Same 'a patch path outranks a no-bump one'        (Verdict @('tests/Check.cs', 'src/AS2.ModApi/Str.cs')) 'patch'
Same 'a minor path outranks a no-bump one'        (Verdict @('.github/workflows/x.yml', 'README.md')) 'minor'
Same 'every kind at once is still a minor'        (Verdict @('tests/a.cs', 'src/b.cs', 'docs/c.md')) 'minor'

# Blank lines come back from git's output on an empty diff, and counting one as a changed file
# would turn "nothing changed" into a patch release on every run.
Same 'blank lines in the diff are not files'      (Verdict @('', $null, '')) 'none'

# A git that failed is not a git that reported nothing. Treating the two the same would publish
# "nothing changed" for a diff that never ran.
Same 'a failed diff is not the same as an empty one' (Verdict @('src/x.cs') 1) ''
True 'and it says the rules were skipped' (@($reasons | Where-Object { $_ -like '*path rules were skipped*' }).Count -eq 1)

# ---- Summary -----------------------------------------------------------------------------------

Write-Output ''
if ($script:Failures.Count -eq 0) {
    Write-Output "All $($script:Passed) checks passed."
    exit 0
}

Write-Output "$($script:Failures.Count) FAILED ($($script:Passed) passed):"
foreach ($f in $script:Failures) { Write-Output "  - $f" }
exit 1
