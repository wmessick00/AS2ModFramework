<#
.SYNOPSIS
    Decides the next version for a release, from what actually changed since the last one.

.DESCRIPTION
    The version used to be a hand edit of a const in one source file, which tools\pack.ps1 grepped
    to name the archive and the tag. Two things went wrong with that. A forgotten edit publishes a
    tag that collides with the last one, and a remembered edit still picks the segment by feel --
    so a library other mods compile against could lose a public member in a release the number
    called a patch.

    This decides it instead. Two rules, in order, and the first one that fires wins.

      1. The public surface of the assembly about to ship, against the assembly in the last
         release. A signature that was there and is gone now is a major. Signatures added and none
         removed is a minor. Identical falls through to rule 2.

      2. Which paths the diff since the last release tag touched. A path in -MinorPaths is a minor,
         a path in -NoBumpPaths counts for nothing, and anything else tracked is a patch. Every
         changed file landing in -NoBumpPaths means nothing shipped changed, which is the 'none'
         verdict and stops the release.

    Rule 1 is the whole contract for a library like AS2.ModApi. It is nearly empty for a plugin,
    whose types are mostly internal, which is what rule 2 is for: a plugin's contract is the file
    formats and schemas it reads and writes, and those are ordinary internal C# that no surface
    scan can see.

    Nothing here writes anything. It reads two assemblies and a git log and returns a verdict. The
    caller decides what to do with it.

.PARAMETER RepoRoot
    The repository. Used for the git diff and as the base for relative paths.

.PARAMETER Assembly
    The freshly built DLL, whose public surface is compared against the last released one.

.PARAMETER CurrentVersion
    The version constant as it stands on disk right now.

.PARAMETER AssetPattern
    The release asset carrying the DLL, as a glob for gh release download. Example: AS2ModApi-*.zip

.PARAMETER AssetEntry
    Path of the DLL inside that asset, with forward slashes.
    Example: BepInEx/plugins/AS2.ModApi.dll

.PARAMETER PreviousAssembly
    Skip the download and compare against this DLL instead. For testing the comparison by hand.

.PARAMETER MinorPaths
    Repo-relative globs whose change means a minor. Forward slashes. Note that a single asterisk
    crosses a directory separator here, so src/* is already recursive.

.PARAMETER NoBumpPaths
    Repo-relative globs whose change means nothing shipped. Same matching as MinorPaths.

.PARAMETER CecilPath
    Mono.Cecil.dll. Defaults to the pack staging area, then the installed game's BepInEx.

.PARAMETER Bump
    Skip the rules and step the current version by this level. Also the way past a none verdict
    when you know the release is worth cutting anyway.

.PARAMETER ForceVersion
    Skip the rules and use this version. Beats -Bump.

.OUTPUTS
    A single object:

      Current   the version passed in
      Next      the version to ship
      Level     major | minor | patch | none | first | ahead
      Reasons   the evidence, one line each, ready to print

    'first' means no previous release was found, so there is nothing to compare and nothing to diff
    from: ship the constant as it stands. 'ahead' means the constant is already past the last
    published version, so somebody bumped it by hand or a previous run bumped it and then failed to
    push. Either way it ships untouched rather than being bumped twice.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$CurrentVersion,
    [string]$AssetPattern = '',
    [string]$AssetEntry = '',
    [string]$PreviousAssembly = '',
    [string[]]$MinorPaths = @(),
    [string[]]$NoBumpPaths = @(),
    [string]$CecilPath = '',
    [string]$AudiosurfDir = '',
    [ValidateSet('major', 'minor', 'patch')][string]$Bump = '',
    [string]$ForceVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Assembly)) { throw "Assembly not found: $Assembly" }

$reasons = New-Object System.Collections.Generic.List[string]

# ---- Semantic versions --------------------------------------------------------------------------
#
# Three numbers, nothing else. No pre-release and no build metadata: every version these repos have
# published is x.y.z, and a tag is formed as "v$version", so accepting more here would only create
# shapes the rest of the pipeline cannot name.

function ConvertTo-SemVer($text) {
    if ($text -notmatch '^\s*(\d+)\.(\d+)\.(\d+)\s*$') {
        throw "Not a three-part version: '$text'"
    }
    return [pscustomobject]@{
        Major = [int]$Matches[1]
        Minor = [int]$Matches[2]
        Patch = [int]$Matches[3]
    }
}

function Format-SemVer($v) { return "$($v.Major).$($v.Minor).$($v.Patch)" }

function Compare-SemVer($a, $b) {
    if ($a.Major -ne $b.Major) { return $a.Major - $b.Major }
    if ($a.Minor -ne $b.Minor) { return $a.Minor - $b.Minor }
    return $a.Patch - $b.Patch
}

function Step-SemVer($v, $level) {
    switch ($level) {
        'major' { return [pscustomobject]@{ Major = $v.Major + 1; Minor = 0;            Patch = 0 } }
        'minor' { return [pscustomobject]@{ Major = $v.Major;     Minor = $v.Minor + 1; Patch = 0 } }
        'patch' { return [pscustomobject]@{ Major = $v.Major;     Minor = $v.Minor;     Patch = $v.Patch + 1 } }
    }
    throw "Not a bump level: '$level'"
}

$current = ConvertTo-SemVer $CurrentVersion

function New-Verdict($level, $next) {
    return [pscustomobject]@{
        Current = Format-SemVer $current
        Next    = $next
        Level   = $level
        Reasons = $reasons.ToArray()
    }
}

# ---- Mono.Cecil ---------------------------------------------------------------------------------
#
# Not a NuGet dependency, matching the rest of these repos. Cecil ships inside BepInEx, so it is
# already on disk in both places this ever runs: the pack staging area during a framework release,
# and the installed game everywhere else. Both plugin packs already refuse to run without the game.

if (-not $CecilPath) {
    if (-not $AudiosurfDir) {
        $AudiosurfDir = if ($env:AudiosurfDir) { $env:AudiosurfDir }
                        else { 'C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2' }
    }
    $candidates = @(
        (Join-Path $RepoRoot 'build\stage\BepInEx\core\Mono.Cecil.dll'),
        (Join-Path $AudiosurfDir 'BepInEx\core\Mono.Cecil.dll')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $CecilPath = $c; break } }
}
if (-not $CecilPath -or -not (Test-Path $CecilPath)) {
    throw "Mono.Cecil.dll not found. Pass -CecilPath, or -AudiosurfDir pointing at the game."
}

Add-Type -Path $CecilPath | Out-Null

# ---- The public surface -------------------------------------------------------------------------
#
# Every member another assembly can bind to, rendered as a string, sorted into a set. Two sets and a
# difference is the whole of rule 1.
#
# Methods and fields only. A property and an event are sugar over accessor methods that are already
# public when the member is, so the method pass covers them -- a removed public property shows up as
# get_Foo going missing, which is the same fact said in IL terms.
#
# Field constants are rendered by signature and never by value. The version constant is itself a
# public field, so rendering values would make every single release a surface change and this rule
# would report a minor forever.
#
# Protected is included alongside public. A protected member is part of the contract for anyone
# subclassing the type, and it costs nothing to be conservative here.

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

# A nested type is only reachable if every type enclosing it is reachable too, so this walks out to
# the top rather than testing the one flag.
function Test-IsVisibleType($type) {
    $t = $type
    while ($null -ne $t) {
        if ($t.IsNested) {
            if (-not ($t.IsNestedPublic -or $t.IsNestedFamily)) { return $false }
            $t = $t.DeclaringType
            continue
        }
        return $t.IsPublic
    }
    return $false
}

function Get-TypeName($typeRef) {
    if ($null -eq $typeRef) { return '<none>' }
    return $typeRef.FullName
}

function Get-PublicSurface($assemblyPath) {
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($assemblyPath)
    try {
        $set = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)

        foreach ($type in (Get-AllTypes $module)) {
            if (-not (Test-IsVisibleType $type)) { continue }

            # The type line carries the base type and the interfaces, so a type that stops
            # implementing something reads as a removal rather than as nothing at all.
            $kind = if ($type.IsInterface) { 'interface' }
                    elseif ($type.IsEnum)  { 'enum' }
                    else                   { 'type' }
            $ifaces = @()
            foreach ($i in $type.Interfaces) { $ifaces += (Get-TypeName $i.InterfaceType) }
            $ifaceText = if ($ifaces.Count -gt 0) { ' : ' + (($ifaces | Sort-Object) -join ', ') } else { '' }
            $set.Add("$kind $($type.FullName) < $(Get-TypeName $type.BaseType)$ifaceText") | Out-Null

            foreach ($m in $type.Methods) {
                if (-not ($m.IsPublic -or $m.IsFamily)) { continue }
                $params = @()
                foreach ($p in $m.Parameters) { $params += (Get-TypeName $p.ParameterType) }
                $generics = if ($m.HasGenericParameters) { "``$($m.GenericParameters.Count)" } else { '' }
                $static = if ($m.IsStatic) { 'static ' } else { '' }
                $set.Add("$static$($type.FullName)::$($m.Name)$generics($($params -join ', ')) : $(Get-TypeName $m.ReturnType)") | Out-Null
            }

            foreach ($f in $type.Fields) {
                if (-not ($f.IsPublic -or $f.IsFamily)) { continue }
                $static = if ($f.IsStatic) { 'static ' } else { '' }
                $set.Add("$static$($type.FullName)::$($f.Name) : $(Get-TypeName $f.FieldType)") | Out-Null
            }
        }

        return $set
    }
    finally {
        $module.Dispose()
    }
}


# ---- Native commands ----------------------------------------------------------------------------
#
# Windows PowerShell turns a native command's stderr into an ErrorRecord, and with
# $ErrorActionPreference = Stop that ends the script. gh writes to stderr for answers that are not
# errors here at all: "release not found" is the ordinary first-release case, and it has to come
# back as a value rather than as a throw. So every native call goes through this, where only the
# exit code decides.

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>$null
        $code = $LASTEXITCODE
        # Clear it deliberately. A failure here is an answer, not a fault -- git describe exits 128
        # in a repo with no tags, which is the first-release case -- and pack.ps1 tests $LASTEXITCODE
        # after its own dotnet build calls. Leaving 128 sitting there would fail the next build that
        # actually succeeded. The exit code the caller wants is on the returned object.
        $global:LASTEXITCODE = 0
        return [pscustomobject]@{ ExitCode = $code; Output = $output }
    }
    finally { $ErrorActionPreference = $previous }
}
# ---- The last release ---------------------------------------------------------------------------
#
# What was actually published, not what a local tag claims. gh is asked first, because a tag can sit
# in a working clone with no release behind it. The tag is the fallback for a machine with no gh and
# no network, which still gets rule 2 even though rule 1 needs the DLL out of the release.

# gh has to be told which repo when the remote is not called origin. AS2-MusicFolders names its
# remote after itself, so reading the slug off the tracked upstream's remote is the only thing that
# works in all three repos.
function Get-RepoSlug {
    $r = Invoke-Native git @('-C', $RepoRoot, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}')
    if ($r.ExitCode -ne 0 -or -not $r.Output) { return '' }
    $upstream = "$($r.Output)".Trim()
    $remote = ($upstream -split '/')[0]
    $r = Invoke-Native git @('-C', $RepoRoot, 'remote', 'get-url', $remote)
    if ($r.ExitCode -ne 0 -or -not $r.Output) { return '' }
    $url = "$($r.Output)".Trim()
    if ($url -match '[:/]([^/:]+)/([^/]+?)(\.git)?\s*$') { return "$($Matches[1])/$($Matches[2])" }
    return ''
}

function Get-LastRelease {
    $slug = Get-RepoSlug
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh -and $slug) {
        $r = Invoke-Native gh @('release', 'view', '--repo', $slug, '--json', 'tagName')
        if ($r.ExitCode -eq 0 -and $r.Output) {
            $tag = (($r.Output -join '') | ConvertFrom-Json).tagName
            if ($tag) { return $tag }
        }
    }

    $r = Invoke-Native git @('-C', $RepoRoot, 'describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*')
    if ($r.ExitCode -eq 0 -and $r.Output) { return "$($r.Output)".Trim() }

    return ''
}

function Get-PreviousAssembly($tag) {
    if (-not $AssetPattern -or -not $AssetEntry) { return '' }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { return '' }
    $slug = Get-RepoSlug
    if (-not $slug) { return '' }

    $cache = Join-Path $RepoRoot 'build\cache\prev'
    if (Test-Path $cache) { Remove-Item $cache -Recurse -Force }
    New-Item -ItemType Directory -Path $cache -Force | Out-Null

    $r = Invoke-Native gh @('release', 'download', $tag, '--repo', $slug, '--pattern', $AssetPattern, '--dir', $cache)
    if ($r.ExitCode -ne 0) { return '' }

    $zip = Get-ChildItem $cache -Filter *.zip | Select-Object -First 1
    if (-not $zip) { return '' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try {
        # Not GetEntry. The two pack scripts disagree about the separator inside the archive: this
        # repo's uses CreateFromDirectory, which on Windows PowerShell writes BepInEx\plugins\... with
        # backslashes, while the plugin repos write their entries by hand with the forward slashes the
        # ZIP spec calls for. Released archives of both shapes exist and neither can be rewritten now,
        # so the name is matched with both separators folded together.
        $wanted = $AssetEntry.Replace('\', '/')
        $entry = $null
        foreach ($e in $archive.Entries) {
            if ($e.FullName.Replace('\', '/') -eq $wanted) { $entry = $e; break }
        }
        if ($null -eq $entry) { return '' }
        $out = Join-Path $cache ([IO.Path]::GetFileName($AssetEntry))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
        return $out
    }
    finally {
        $archive.Dispose()
    }
}

# ---- Rule 2: the paths --------------------------------------------------------------------------
#
# git reports forward slashes, so the globs are written with forward slashes too. PowerShell's -like
# lets an asterisk cross a separator, which means docs/* already matches docs/reference/probe-dump.txt
# and no ** form is needed.

function Test-MatchesAny($path, $patterns) {
    foreach ($p in $patterns) { if ($path -like $p) { return $true } }
    return $false
}

function Get-PathVerdict($tag) {
    $r = Invoke-Native git @('-C', $RepoRoot, 'diff', '--name-only', "$tag..HEAD")
    if ($r.ExitCode -ne 0) {
        $reasons.Add("could not diff $tag..HEAD, so the path rules were skipped")
        return ''
    }

    $changed = @($r.Output | Where-Object { $_ })
    if ($changed.Count -eq 0) {
        $reasons.Add("no file changed since $tag")
        return 'none'
    }

    $minor = New-Object System.Collections.Generic.List[string]
    $patch = New-Object System.Collections.Generic.List[string]
    $none  = New-Object System.Collections.Generic.List[string]

    foreach ($f in $changed) {
        if (Test-MatchesAny $f $MinorPaths)  { $minor.Add($f); continue }
        if (Test-MatchesAny $f $NoBumpPaths) { $none.Add($f);  continue }
        $patch.Add($f)
    }

    $reasons.Add("$($changed.Count) file(s) changed since ${tag}: $($minor.Count) minor, $($patch.Count) patch, $($none.Count) no-bump")

    foreach ($f in $minor) { $reasons.Add("  minor    $f  (declared contract path)") }
    foreach ($f in $patch) { $reasons.Add("  patch    $f") }
    foreach ($f in $none)  { $reasons.Add("  no-bump  $f") }

    if ($minor.Count -gt 0) { return 'minor' }
    if ($patch.Count -gt 0) { return 'patch' }
    return 'none'
}

# ---- Decide -------------------------------------------------------------------------------------

# An override is taken at its word, and the rules are not run at all. Working out what the rules
# would have said costs a release download to answer a question that has already been settled, and
# reporting a verdict that was then ignored reads as though it mattered.
if ($ForceVersion) {
    if ($ForceVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Not a three-part version: '$ForceVersion'" }
    $reasons.Add("-ForceVersion given, so the rules were not run")
    return New-Verdict 'override' $ForceVersion
}

if ($Bump) {
    $reasons.Add("-Bump $Bump given, so the rules were not run")
    return New-Verdict $Bump (Format-SemVer (Step-SemVer $current $Bump))
}

$lastTag = Get-LastRelease

if (-not $lastTag) {
    $reasons.Add('no previous release found, so this is the first one')
    $reasons.Add("shipping the constant as it stands: $(Format-SemVer $current)")
    return New-Verdict 'first' (Format-SemVer $current)
}

$reasons.Add("last release   $lastTag")

# Idempotence. If the constant is already past what was published, somebody took it there on
# purpose or a previous run bumped it and then failed to push. Bumping again would skip a number,
# and worse, it would keep skipping one on every retry.
$lastVersion = $null
if ($lastTag -match '^v?(\d+\.\d+\.\d+)$') { $lastVersion = ConvertTo-SemVer $Matches[1] }

if ($null -ne $lastVersion -and (Compare-SemVer $current $lastVersion) -gt 0) {
    $reasons.Add("the constant is already ahead of $lastTag, so it ships untouched")
    return New-Verdict 'ahead' (Format-SemVer $current)
}

# Rule 1.
$prev = if ($PreviousAssembly) { $PreviousAssembly } else { Get-PreviousAssembly $lastTag }

if ($prev -and (Test-Path $prev)) {
    $before = Get-PublicSurface $prev
    $after  = Get-PublicSurface $Assembly

    $removed = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    foreach ($s in $before) { if (-not $after.Contains($s))  { $removed.Add($s) | Out-Null } }
    $added = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    foreach ($s in $after)  { if (-not $before.Contains($s)) { $added.Add($s)   | Out-Null } }

    $reasons.Add("public surface $($before.Count) member(s) before, $($after.Count) now: $($added.Count) added, $($removed.Count) removed")

    if ($removed.Count -gt 0) {
        foreach ($s in $removed) { $reasons.Add("  - $s") }
        foreach ($s in $added)   { $reasons.Add("  + $s") }
        return New-Verdict 'major' (Format-SemVer (Step-SemVer $current 'major'))
    }
    if ($added.Count -gt 0) {
        foreach ($s in $added) { $reasons.Add("  + $s") }
        return New-Verdict 'minor' (Format-SemVer (Step-SemVer $current 'minor'))
    }

    $reasons.Add('public surface unchanged, falling through to the path rules')
}
else {
    $reasons.Add('no previous assembly to compare against, so a removed public member cannot be seen here')
}

# Rule 2.
$level = Get-PathVerdict $lastTag

if (-not $level -or $level -eq 'none') {
    $reasons.Add('nothing that ships changed')
    return New-Verdict 'none' (Format-SemVer $current)
}

return New-Verdict $level (Format-SemVer (Step-SemVer $current $level))
