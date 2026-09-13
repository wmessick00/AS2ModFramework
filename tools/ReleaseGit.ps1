<#
.SYNOPSIS
    The git and GitHub plumbing a release needs. Dot-sourced by tools\pack.ps1.

.DESCRIPTION
    Publishing writes the version constant, commits it, pushes it, and only then creates the
    release. The order matters: gh release create tags whatever the remote's default branch points
    at, so a bump that is written but not pushed produces a tag whose source says the previous
    version. The whole point of reading the version off a constant is that a downloaded file name
    and a user's log line agree, and an unpushed bump quietly breaks exactly that.

    That same ordering is why a publish only runs on the default branch. The push follows the
    tracked upstream and the tag follows the repository's default branch, so on any other branch
    the bump lands one place and the release is cut from another. Assert-PublishReady compares the
    two before the build starts, and pack.ps1 names the branch on gh release create rather than
    leaving it implied.

    Everything here is checked before anything is built. A release that fails on the push has
    already replaced the maintainer's build output and rewritten a tracked source file, so the
    checks that can run early do run early.

    The remote is never assumed to be called origin. AS2-MusicFolders names its remote after itself,
    so the remote and the branch are read off the tracked upstream every time.
#>

# No Set-StrictMode here. This file is dot-sourced, so it would land in pack.ps1 scope and change
# how that script treats every unset property it has ever had -- a side effect a library has no
# business having on the script that loads it.

# Windows PowerShell turns a native command's stderr into an ErrorRecord, and with
# $ErrorActionPreference = Stop that ends the script. git and gh both write to stderr for answers
# that are not faults here, so native calls go through this and only the exit code decides.
# Get-NextVersion.ps1 carries its own copy of this rather than dot-sourcing this file, so it stays
# runnable on its own for testing a verdict without a release in progress.
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # 2>&1, not 2>$null. Windows PowerShell turns a redirected native stderr line into an
        # ErrorRecord, which is the whole reason this runs under Continue -- under Stop it ends the
        # script. Merged, both streams arrive in one list and the record type tells them apart, so
        # stdout stays exactly what every existing caller already reads and stderr becomes readable
        # instead of thrown away.
        #
        # Not 2>$someFile, which also survives here but writes PowerShell's decorated rendering of
        # the record into it -- the "At C:\...\ReleaseGit.ps1:40 char:19" block and the
        # CategoryInfo lines, around the one sentence that mattered. That is a worse diagnostic
        # than none. Checked both ways against git and cmd under 5.1 before choosing.
        $merged = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
        $global:LASTEXITCODE = 0

        $out = @()
        $err = @()
        foreach ($line in $merged) {
            if ($line -is [System.Management.Automation.ErrorRecord]) { $err += $line.ToString() }
            else { $out += $line }
        }

        return [pscustomobject]@{ ExitCode = $code; Output = $out; Error = $err }
    }
    finally { $ErrorActionPreference = $previous }
}

# The reason a native command gave, for the message a caller throws.
#
# Issue AS2-MusicFolders #36, and this file is shared, so the fix is shared. git and gh write the
# part a maintainer needs -- a rejected push, an auth failure, a hook that refused the commit -- to
# stderr, and every throw below used to be built from stdout alone. The sentence arrived with a
# blank line under it and the run had to be repeated by hand to find out why.
#
# Falls back to stdout, then to saying plainly that there was nothing, because a message that
# trails off is what this is here to stop.
function Get-NativeReason($result) {
    if ($null -eq $result) { return '(no result)' }

    $err = @($result.Error | Where-Object { $_ -and "$_".Trim() })
    if ($err.Count -gt 0) { return ($err -join [Environment]::NewLine) }

    $out = @($result.Output | Where-Object { $_ -and "$_".Trim() })
    if ($out.Count -gt 0) { return ($out -join [Environment]::NewLine) }

    return '(the command printed nothing)'
}

# Remote, branch and owner/name, all off the tracked upstream. Returns $null when the branch tracks
# nothing, which is the case the caller has to report rather than work around.
function Get-Upstream($repoRoot) {
    $r = Invoke-Native git @('-C', $repoRoot, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}')
    if ($r.ExitCode -ne 0 -or -not $r.Output) { return $null }

    $upstream = "$($r.Output)".Trim()
    $remote = ($upstream -split '/')[0]
    $branch = $upstream.Substring($remote.Length + 1)

    $r = Invoke-Native git @('-C', $repoRoot, 'remote', 'get-url', $remote)
    if ($r.ExitCode -ne 0 -or -not $r.Output) { return $null }

    $url = "$($r.Output)".Trim()
    $slug = ''
    if ($url -match '[:/]([^/:]+)/([^/]+?)(\.git)?\s*$') { $slug = "$($Matches[1])/$($Matches[2])" }

    return [pscustomobject]@{ Remote = $remote; Branch = $branch; Url = $url; Slug = $slug }
}

# The branch the repository itself releases from. gh release create is given no --target by default,
# so this is the branch it tags whatever the local checkout is sitting on. Asked of the repository
# rather than hard-coded to main: this file is shared verbatim by three repositories, and a name
# that is wrong for one of them would fail open, which is the failure this check exists to close.
function Get-DefaultBranch($slug) {
    $r = Invoke-Native gh @('repo', 'view', $slug, '--json', 'defaultBranchRef', '--jq', '.defaultBranchRef.name')
    if ($r.ExitCode -ne 0 -or -not $r.Output) {
        throw @"
Could not read the default branch of $slug. -Publish needs it to know the tag lands on the commit
it just pushed.

$(Get-NativeReason $r)
"@
    }
    return "$($r.Output)".Trim()
}

# Every reason a publish cannot finish, reported in one place and before the build starts.
function Assert-PublishReady($repoRoot) {
    $r = Invoke-Native git @('-C', $repoRoot, 'rev-parse', '--is-inside-work-tree')
    if ($r.ExitCode -ne 0) {
        throw @"
-Publish needs a git repository, and $repoRoot is not one. The version bump has to be committed and
pushed before the tag is created.

$(Get-NativeReason $r)
"@
    }

    $r = Invoke-Native git @('-C', $repoRoot, 'status', '--porcelain')
    if ($r.ExitCode -ne 0) { throw "git status failed in $repoRoot`n`n$(Get-NativeReason $r)" }
    $dirty = @($r.Output | Where-Object { $_ })
    if ($dirty.Count -gt 0) {
        throw @"
The working tree is not clean, so the version bump cannot be committed on its own:

$($dirty -join "`n")

Commit or stash these first. A release commit that carries unrelated changes is not something
this script should decide to make for you.
"@
    }

    $upstream = Get-Upstream $repoRoot
    if ($null -eq $upstream) {
        throw "The current branch tracks no upstream, so the bump cannot be pushed. Set one with: git push -u <remote> <branch>"
    }
    if (-not $upstream.Slug) {
        throw "Could not read an owner/name out of the remote URL '$($upstream.Url)'. gh needs one to find the repository."
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw @"
-Publish needs the GitHub CLI, which is not installed.

Install it from https://cli.github.com/ and re-run, or pack without -Publish: that prints the
files to attach and the tag to attach them to, for uploading by hand.
"@
    }

    $r = Invoke-Native gh @('auth', 'status')
    if ($r.ExitCode -ne 0) {
        throw @"
The GitHub CLI is installed but not signed in. Run: gh auth login

$(Get-NativeReason $r)
"@
    }

    # Last, because it is the one check that needs the network and the sign-in above. The bump is
    # pushed to the tracked upstream and the tag is created on the default branch, and until here
    # nothing has ever said those are the same branch. AS2-SkinSettings issue #47, and this
    # file is shared, so the fix is shared.
    $default = Get-DefaultBranch $upstream.Slug
    if ($upstream.Branch -ne $default) {
        throw @"
The current branch tracks $($upstream.Remote)/$($upstream.Branch), but $($upstream.Slug) releases from $default.

The bump would be pushed to $($upstream.Branch) and the tag created on $default, so the release
would carry a version constant the tagged commit does not have -- which is the disagreement
between a file name and a log line that reading the version off a constant exists to prevent.

Merge this branch into $default and re-run there, or pack without -Publish to get the archive and
upload it by hand.
"@
    }

    return $upstream
}

# The bump, as its own commit, pushed before the release is created. Nothing else is staged: the
# working tree was asserted clean above, so this can only ever carry the one file.
function Save-VersionBump($repoRoot, $versionFile, $version, $upstream) {
    $relative = $versionFile
    if ($relative.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $relative.Substring($repoRoot.Length).TrimStart('\', '/')
    }

    $r = Invoke-Native git @('-C', $repoRoot, 'add', '--', $relative)
    if ($r.ExitCode -ne 0) { throw "git add failed for $relative`n`n$(Get-NativeReason $r)" }

    $r = Invoke-Native git @('-C', $repoRoot, 'commit', '-m', "Take the version to $version")
    if ($r.ExitCode -ne 0) { throw "git commit failed for the version bump.`n`n$(Get-NativeReason $r)" }

    $r = Invoke-Native git @('-C', $repoRoot, 'push', $upstream.Remote, "HEAD:$($upstream.Branch)")
    if ($r.ExitCode -ne 0) {
        throw @"
The version bump is committed but the push to $($upstream.Remote)/$($upstream.Branch) failed.

Nothing has been released. Push it yourself and re-run -- the bump is already in the constant, so
the next run ships $version rather than bumping again.

$(Get-NativeReason $r)
"@
    }
}
