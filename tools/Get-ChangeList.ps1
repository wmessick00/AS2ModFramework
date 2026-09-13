<#
.SYNOPSIS
    Builds the change list for a release from the pull requests merged since the last one.

.DESCRIPTION
    This repository writes commit subjects and pull request titles as plain sentences -- "Write
    archives a mod manager can read", "Lock the store every mod writes its data through". They
    already read the way a changelog entry should, so the changelog is assembled from them rather
    than written twice.

    Pull requests, not commits, because a pull request is the unit somebody actually did. #68 landed
    as four commits; as a changelog that is four bullets describing one change.

    A repository that pushed straight to main would produce no merge commits at all, so the commit
    subjects are the fallback. The version bump commit is dropped from those -- "Take the version to
    0.2.3" is the release, not a thing in it.

    What this deliberately does not do is invent prose. It repeats what was already written on the
    pull request. If a title is not worth showing somebody deciding whether to update, the fix is a
    better title, or pack.ps1's -ChangeLogText for that release.

.PARAMETER RepoRoot
    The repository.

.PARAMETER FromTag
    The previous release tag. Everything merged after it is in this release.

.PARAMETER Slug
    owner/name, for looking up pull request titles. Without it, only the commit fallback runs.

.EXAMPLE
    .\tools\Get-ChangeList.ps1 -RepoRoot . -FromTag v0.2.2 -Slug wmessick00/AS2ModFramework
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [string]$FromTag,
    [string]$Slug
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseGit.ps1')

# ---- The parsing, kept separate so tests\Check-ReleaseTooling.ps1 can drive it ------------------

<#
.SYNOPSIS
    The pull request numbers named by a list of merge commit subjects
#>
# GitHub writes "Merge pull request #68 from owner/branch". Anything else in the merge log is a
# branch catching up with main -- this repository produces a lot of "Merge main into <branch>" --
# and naming no pull request is exactly what tells those apart.
function Get-MergedPullRequestNumber {
    param([string[]]$Subject = @())

    $numbers = New-Object System.Collections.Generic.List[int]

    foreach ($line in $Subject) {
        if ($null -eq $line) { continue }
        if ($line -match '^Merge pull request #(\d+)\b') {
            $n = [int]$Matches[1]
            if (-not $numbers.Contains($n)) { $numbers.Add($n) }
        }
    }

    return ,$numbers.ToArray()
}

<#
.SYNOPSIS
    Commit subjects worth showing, for the repository with no merge commits
#>
function Select-ChangeSubject {
    param([string[]]$Subject = @())

    $kept = New-Object System.Collections.Generic.List[string]

    foreach ($line in $Subject) {
        if ($null -eq $line) { continue }
        $trimmed = $line.Trim()
        if (-not $trimmed) { continue }

        # The release itself, written by Save-VersionBump. It is not a change in the release.
        if ($trimmed -match '^Take the version to \d+\.\d+\.\d+$') { continue }

        # Merge commits of every shape. The pull request path already had its chance at these, and
        # "Merge main into <branch>" is bookkeeping either way.
        if ($trimmed -match '^Merge ') { continue }

        if (-not $kept.Contains($trimmed)) { $kept.Add($trimmed) }
    }

    return ,$kept.ToArray()
}

<#
.SYNOPSIS
    Turn titles into the bullet list that goes in the release body
#>
function Format-ChangeList {
    param([string[]]$Title = @())

    $lines = New-Object System.Collections.Generic.List[string]

    foreach ($t in $Title) {
        if ($null -eq $t) { continue }
        $trimmed = $t.Trim()
        if (-not $trimmed) { continue }

        # No terminal period. These are fragments naming a change, and the titles they come from do
        # not carry one either.
        $trimmed = $trimmed.TrimEnd('.')
        $lines.Add("- $trimmed")
    }

    return ($lines -join "`n")
}

# ---- Reading the repository --------------------------------------------------------------------

function Get-MergeSubject($from) {
    $range = if ($from) { "$from..HEAD" } else { 'HEAD' }
    $r = Invoke-Native git @('-C', $RepoRoot, 'log', $range, '--merges', '--format=%s')
    if ($r.ExitCode -ne 0) { return @() }
    return @($r.Output)
}

function Get-CommitSubject($from) {
    $range = if ($from) { "$from..HEAD" } else { 'HEAD' }
    $r = Invoke-Native git @('-C', $RepoRoot, 'log', $range, '--no-merges', '--format=%s')
    if ($r.ExitCode -ne 0) { return @() }
    return @($r.Output)
}

# One call for every title, rather than one per pull request. A release with a dozen merged pull
# requests should not be a dozen round trips while somebody watches a release run.
function Get-PullRequestTitle($numbers) {
    if (-not $Slug -or $numbers.Count -eq 0) { return @{} }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return @{} }

    $r = Invoke-Native gh @('pr', 'list', '--repo', $Slug, '--state', 'merged',
                            '--limit', '200', '--json', 'number,title')
    if ($r.ExitCode -ne 0) { return @{} }

    $titles = @{}
    try {
        foreach ($pr in (($r.Output -join '') | ConvertFrom-Json)) { $titles[[int]$pr.number] = $pr.title }
    }
    catch { return @{} }

    return $titles
}

# ---- The answer --------------------------------------------------------------------------------

$numbers = Get-MergedPullRequestNumber (Get-MergeSubject $FromTag)
$titles  = Get-PullRequestTitle $numbers

$wanted = New-Object System.Collections.Generic.List[string]
foreach ($n in $numbers) {
    if ($titles.ContainsKey($n)) { $wanted.Add($titles[$n]) }
}

# Falls back when there were no merge commits, and also when gh could not be reached. A release note
# assembled from commit subjects is worse than one from pull request titles and much better than
# none.
if ($wanted.Count -eq 0) {
    foreach ($s in (Select-ChangeSubject (Get-CommitSubject $FromTag))) { $wanted.Add($s) }
}

return Format-ChangeList $wanted.ToArray()
