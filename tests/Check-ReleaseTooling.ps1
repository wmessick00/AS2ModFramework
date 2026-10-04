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

# ---- The change list ----------------------------------------------------------------------------
#
# Lifted the same way, and for the same reason: Get-ChangeList.ps1 has a param block and ends by
# returning an answer, so dot-sourcing it would run a release's worth of git and gh.

$changeListScript = Join-Path $repoRoot 'tools\Get-ChangeList.ps1'
if (-not (Test-Path $changeListScript)) { throw "Not found: $changeListScript" }

$clErrors = $null
$clAst = [System.Management.Automation.Language.Parser]::ParseFile($changeListScript, [ref]$null, [ref]$clErrors)
if ($clErrors -and $clErrors.Count -gt 0) {
    throw "tools\Get-ChangeList.ps1 does not parse: $($clErrors[0].Message)"
}

$clWanted = @('Get-MergedPullRequestNumber', 'Select-ChangeSubject', 'Format-ChangeList')
$clFound = @{}
foreach ($fn in $clAst.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($clWanted -contains $fn.Name) { $clFound[$fn.Name] = $fn.Extent.Text }
}
foreach ($name in $clWanted) {
    if (-not $clFound.ContainsKey($name)) {
        throw "Get-ChangeList.ps1 no longer defines $name. If it was renamed, rename it here too."
    }
    Invoke-Expression $clFound[$name]
}

Write-Output ''
Write-Output 'Reading pull request numbers out of the merge log'

Same 'a merge commit names its pull request' `
     (Get-MergedPullRequestNumber @('Merge pull request #68 from wmessick00/some-branch'))[0] 68

# This repository produces a lot of these, and every one of them would otherwise become a bullet
# saying nothing.
Same 'a branch catching up with main is not a change' `
     (Get-MergedPullRequestNumber @('Merge main into check-the-script')).Count 0

Same 'an octopus or a hand-written merge is skipped too' `
     (Get-MergedPullRequestNumber @("Merge branch 'main' of github.com:owner/repo")).Count 0

Same 'several merges come back in order' `
     ((Get-MergedPullRequestNumber @(
        'Merge pull request #69 from a/b',
        'Merge main into b',
        'Merge pull request #68 from a/c')) -join ',') '69,68'

# A branch merged into main twice, or a pull request reopened, should not be listed twice.
Same 'the same pull request is listed once' `
     (Get-MergedPullRequestNumber @('Merge pull request #68 from a/b', 'Merge pull request #68 from a/b')).Count 1

Same 'nothing merged is no numbers' (Get-MergedPullRequestNumber @()).Count 0
Same 'a null line does not throw' (Get-MergedPullRequestNumber @($null, 'Merge pull request #7 from a/b')).Count 1

# The number has to be bounded by a word break. "#68x" is not pull request 68.
Same 'a number has to end where it should' (Get-MergedPullRequestNumber @('Merge pull request #68x from a/b')).Count 0

Write-Output ''
Write-Output 'Falling back to commit subjects'

Same 'an ordinary subject is kept' `
     (Select-ChangeSubject @('Write archives a mod manager can read'))[0] 'Write archives a mod manager can read'

# Save-VersionBump writes this one. It is the release, not something in it.
Same 'the version bump commit is dropped' (Select-ChangeSubject @('Take the version to 0.2.3')).Count 0
Same 'but a subject that merely mentions a version is kept' `
     (Select-ChangeSubject @('Take the version to 0.2.3 out of the log line')).Count 1

Same 'merge commits are dropped here as well' `
     (Select-ChangeSubject @('Merge pull request #68 from a/b', 'Merge main into b')).Count 0

Same 'blank subjects are dropped' (Select-ChangeSubject @('', '   ', $null)).Count 0
Same 'a repeated subject is listed once' `
     (Select-ChangeSubject @('Fix the thing', 'Fix the thing')).Count 1

Write-Output ''
Write-Output 'Formatting the list'

Same 'a title becomes a bullet' (Format-ChangeList @('Write archives a mod manager can read')) `
     '- Write archives a mod manager can read'

# The titles in this repository do not carry one, and a changelog of fragments should not either.
Same 'a terminal period is trimmed' (Format-ChangeList @('Fix the thing.')) '- Fix the thing'
Same 'but an ellipsis is not special-cased into nonsense' (Format-ChangeList @('Fix the thing...')) '- Fix the thing'

Same 'several titles are one list' (Format-ChangeList @('First thing', 'Second thing')) `
     "- First thing`n- Second thing"

Same 'nothing in is nothing out' (Format-ChangeList @()) ''
Same 'blank titles contribute nothing' (Format-ChangeList @('', '   ')) ''

# The empty case is what pack.ps1 refuses a publish on, so it has to be empty rather than "- ".
True 'an empty list is falsy, which is what the publish guard tests' (-not (Format-ChangeList @()))

# ---- The change list, end to end: what happens when a title cannot be found --------------------
#
# Issue #82. The titles come from one gh call that returns a page, and a merged pull request that
# was not on the page, or not reachable at all, was dropped without a word: the release body went to
# Nexus one entry short. These run the real script, in this process, against a real repository with
# merge commits in it and a gh that is a function. Git is not faked, because what it is asked is
# the merge log and the log is the fixture.

Write-Output ''
Write-Output 'Looking up pull request titles'

# Where a real git is wanted. A repository with a tag, and one merge commit for each pull request.
$clWork = Join-Path ([IO.Path]::GetTempPath()) ('as2-changelist-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $clWork | Out-Null

function Invoke-Git([string[]]$arguments) {
    & git -C $clRepo -c user.email=check@example.invalid -c user.name=check @arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed in the fixture repository" }
}

function New-MergedRepository([int[]]$numbers) {
    $script:clRepo = Join-Path $clWork ('repo' + (Get-Random))
    New-Item -ItemType Directory -Force -Path $clRepo | Out-Null

    Invoke-Git @('init', '-q')
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'release')
    Invoke-Git @('tag', 'v1.0.0')
    $main = (& git -C $clRepo rev-parse --abbrev-ref HEAD).Trim()

    foreach ($n in $numbers) {
        Invoke-Git @('checkout', '-q', '-b', "work-$n")
        Invoke-Git @('commit', '-q', '--allow-empty', '-m', "work on $n")
        Invoke-Git @('checkout', '-q', $main)
        Invoke-Git @('merge', '-q', '--no-ff', '-m', "Merge pull request #$n from owner/work-$n", "work-$n")
    }
}

# The gh the script sees. A function wins over an executable of the same name, and the script asks
# for it by name; the state it answers from is global because the script runs in a scope of its own.
function global:gh {
    $a = @($args)
    $state = $global:ChangeListGh
    $state.Calls.Add(($a -join ' '))
    $global:LASTEXITCODE = 0

    if ($a[0] -eq 'pr' -and $a[1] -eq 'list') {
        if ($state.ListFails) { $global:LASTEXITCODE = 1; return }
        if ($state.ListRaw)   { return $state.ListRaw }
        return (ConvertTo-Json -Compress -InputObject @($state.OnThePage.GetEnumerator() |
                    ForEach-Object { [pscustomobject]@{ number = $_.Key; title = $_.Value } }))
    }

    if ($a[0] -eq 'pr' -and $a[1] -eq 'view') {
        $n = [int]$a[2]
        if ($state.Elsewhere.ContainsKey($n)) { return (ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ title = $state.Elsewhere[$n] })) }
        $global:LASTEXITCODE = 1
        return
    }

    $global:LASTEXITCODE = 1
}

function Use-Gh($onThePage = @{}, $elsewhere = @{}, [switch]$listFails, $listRaw = $null) {
    $global:ChangeListGh = @{
        Calls = New-Object System.Collections.Generic.List[string]
        OnThePage = $onThePage
        Elsewhere = $elsewhere
        ListFails = [bool]$listFails
        ListRaw = $listRaw
    }
}

function Get-ChangeListResult($slug = 'owner/repo') {
    $warnings = $null
    $arguments = @{ RepoRoot = $clRepo; FromTag = 'v1.0.0'; WarningVariable = 'warnings'; WarningAction = 'SilentlyContinue' }
    if ($slug) { $arguments.Slug = $slug }

    $text = & $changeListScript @arguments
    return [pscustomobject]@{ Text = "$text"; Warnings = @($warnings | ForEach-Object { "$_" }) }
}

try {

    New-MergedRepository @(12, 13, 150)

    # Everything on the page: the ordinary case, and quiet.
    Use-Gh @{ 12 = 'Add the first thing'; 13 = 'Fix the second thing'; 150 = 'Lock the third thing' }
    $r = Get-ChangeListResult
    Same 'every title found is one bullet each' ($r.Text -split "`n").Count 3
    True 'and they are the titles, newest merge first' ($r.Text -like '*- Lock the third thing*')
    Same 'and a clean lookup warns of nothing' $r.Warnings.Count 0
    Same 'and costs one call, not one for each pull request' @($global:ChangeListGh.Calls | Where-Object { $_ -like 'pr list*' }).Count 1
    Same 'because nothing needed asking for by number' @($global:ChangeListGh.Calls | Where-Object { $_ -like 'pr view*' }).Count 0

    # Past the page. The list holds only the most recent --limit, and a pull request older than that
    # was dropped from the release. It is asked for now.
    Use-Gh @{ 12 = 'Add the first thing'; 13 = 'Fix the second thing' } @{ 150 = 'Lock the third thing, from past the page' }
    $r = Get-ChangeListResult
    True 'a pull request the page did not hold is still in the list' ($r.Text -like '*- Lock the third thing, from past the page*')
    Same 'and nothing was dropped' ($r.Text -split "`n").Count 3
    Same 'and it was asked for by number, once' @($global:ChangeListGh.Calls | Where-Object { $_ -like 'pr view 150*' }).Count 1
    Same 'and the page was still one call' @($global:ChangeListGh.Calls | Where-Object { $_ -like 'pr list*' }).Count 1
    Same 'and nothing needed warning about' $r.Warnings.Count 0

    # Not anywhere. Left out, and said.
    Use-Gh @{ 12 = 'Add the first thing'; 13 = 'Fix the second thing' } @{}
    $r = Get-ChangeListResult
    Same 'a pull request that cannot be found anywhere is left out' ($r.Text -split "`n").Count 2
    Same 'and exactly one warning says so' $r.Warnings.Count 1
    True 'naming the pull request' ($r.Warnings.Count -eq 1 -and $r.Warnings[0] -like '*#150*')
    True 'and what that means for the list' ($r.Warnings.Count -eq 1 -and $r.Warnings[0] -like '*not in the change list*')

    # The whole lookup failing is the other way to lose titles, and it was silent as well.
    Use-Gh -listFails
    $r = Get-ChangeListResult
    True 'a gh that cannot list falls back to commit subjects, as it always did' ($r.Text -like '*work on*')
    True 'and says why the titles are not there' (@($r.Warnings | Where-Object { $_ -like '*could not list*' }).Count -eq 1)
    Same 'without a further warning for every pull request, which would bury it' $r.Warnings.Count 1

    Use-Gh -listRaw 'this is not json'
    $r = Get-ChangeListResult
    True 'an answer that is not JSON falls back too' ($r.Text -like '*work on*')
    True 'and says so' (@($r.Warnings | Where-Object { $_ -like '*not a list of pull requests*' }).Count -eq 1)

    # No Slug is a choice, not a failure: the script says in its own help that only the commit
    # fallback runs then.
    Use-Gh
    $r = Get-ChangeListResult -slug ''
    True 'with no Slug the commit subjects are used' ($r.Text -like '*work on*')
    Same 'and nothing is warned about, since nobody asked for titles' $r.Warnings.Count 0
    Same 'and gh was not called at all' $global:ChangeListGh.Calls.Count 0

}
finally {
    Remove-Item function:global:gh -ErrorAction SilentlyContinue
    Remove-Variable -Name ChangeListGh -Scope Global -ErrorAction SilentlyContinue
    try { Remove-Item -Recurse -Force $clWork } catch { }
}

# ---- Summary -----------------------------------------------------------------------------------

Write-Output ''
if ($script:Failures.Count -eq 0) {
    Write-Output "All $($script:Passed) checks passed."
    exit 0
}

Write-Output "$($script:Failures.Count) FAILED ($($script:Passed) passed):"
foreach ($f in $script:Failures) { Write-Output "  - $f" }
exit 1
