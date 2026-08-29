<#
.SYNOPSIS
    Assembles the AS2ModFramework release zip: BepInEx core plus the bootstrap and the API, laid out
    to extract straight into the Audiosurf 2 folder.

    This is the single-download GitHub release, which is deliberately not the same shape as the
    Thunderstore split into AS2ModLoader and AS2ModApi -- that split exists so a mod can declare a
    dependency on the API alone, which a plain zip has no way to express.

.DESCRIPTION
    The point of this script is that nobody downloading a release should have to fetch BepInEx
    separately and then be told to install only part of it. Getting that wrong -- copying the zip's
    winhttp.dll or doorstop_config.ini across -- replaces the community patch's doorstop and breaks
    both projects at once, so it is exactly the step that should not be left to hand.

    BepInEx is redistributed unmodified, straight out of the official release, and only the three
    files that belong to the community patch are dropped. That is deliberate: there is no fork to
    maintain, and anyone can hash the core DLLs against the upstream zip to confirm they are stock.

    Only a machine that owns Audiosurf 2 can run this: AS2.ModApi compile-references the game's own
    Assembly-CSharp, LuaInterface and UnityEngine assemblies, which are not redistributable and
    therefore cannot be checked in or placed on a CI runner. Releases are built here and uploaded;
    users download them and never build anything.

.PARAMETER BepInExZip
    Path to an already-downloaded BepInEx_win_x64 zip. Downloaded to a local cache if omitted.

.PARAMETER AudiosurfDir
    The Audiosurf 2 folder, if it is not in the default Steam library.

.PARAMETER SkipHashCheck
    Skip verifying the BepInEx zip against the pinned hash. Only needed when deliberately packaging
    a different BepInEx version, which also means editing the pins below.

.PARAMETER Publish
    After packing, create the GitHub release and upload the zip and its checksum. Needs the GitHub
    CLI; without it the script prints the manual upload steps instead.

    This is also the only mode that writes anything: it takes the version constant to the value
    tools\Get-NextVersion.ps1 decided, commits it, and pushes it before the release is created.
    Packing without it changes no tracked file.

.PARAMETER Bump
    Override the decided level: major, minor or patch. Also lifts the refusal when the only thing
    that changed since the last release was documentation, tests or CI.

.PARAMETER ReleaseVersion
    Override the version outright. Beats -Bump and the decision both. Three numbers.

    Not -Version. PowerShell variable names are case-insensitive, so a -Version parameter and this
    script's own $version are one variable, and reading the constant would overwrite the override.

.EXAMPLE
    .\tools\pack.ps1
    Builds, downloads BepInEx if needed, and writes build\dist\AS2ModFramework-<version>.zip

.EXAMPLE
    .\tools\pack.ps1 -AudiosurfDir "D:\SteamLibrary\steamapps\common\Audiosurf 2" -Publish
#>
[CmdletBinding()]
param(
    [string]$BepInExZip,
    [string]$AudiosurfDir,
    [string]$Configuration = 'Release',
    [switch]$SkipHashCheck,
    [switch]$Publish,
    [ValidateSet('major', 'minor', 'patch')][string]$Bump,
    [string]$ReleaseVersion
)

$ErrorActionPreference = 'Stop'

# Pinned so a release is reproducible and a swapped upstream asset cannot pass unnoticed.
# Bumping BepInEx means changing all three, plus the version table in README.md.
$BepInExVersion = '5.4.23.5'
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256  = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'

# The community patch owns these. Shipping any of them replaces its UnityDoorstop 3.4.1 with 4.5.0,
# which resolves a different entry point and reads a different ini format.
# See the The Loading Chain wiki page.
$PatchOwnedFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')

$repoRoot = Split-Path -Parent $PSScriptRoot
$stageDir = Join-Path $repoRoot 'build\stage'
$distDir  = Join-Path $repoRoot 'build\dist'
$cacheDir = Join-Path $repoRoot 'build\cache'
$stageCore = Join-Path $stageDir 'BepInEx\core'

. (Join-Path $PSScriptRoot 'ReleaseGit.ps1')

# The one file the version lives in. BepInEx prints it in LogOutput.log because it is an argument
# to [BepInPlugin], so a release filename and a user's log line always agree.
$versionFile = Join-Path $repoRoot 'src\AS2.ModApi\ModApiPlugin.cs'

# The release asset carrying the DLL whose public surface decides a major or a minor.
# AS2.ModApi only. AS2.Bootstrap is the doorstop entry point, not something any mod compiles
# against, so its surface is not a contract and a change in it is not a breaking change.
$assetPattern = 'AS2ModApi-*.zip'
$assetEntry   = 'BepInEx/plugins/AS2.ModApi.dll'

# Rule 2 in Get-NextVersion.ps1, for when the public surface came out identical.
#
# There are no minor paths here on purpose. This repo ships a library, so its contract IS its
# public API, and the surface comparison already owns that verdict. A path table that also claimed
# minor would only ever disagree with it.
$MinorPaths = @()

# Changing any of these cannot change the bytes in the archive, so a release built from them would
# be identical to the one before it and the new number would be a lie.
# Note that tools\ is NOT here: README.txt and THIRD-PARTY-NOTICES.txt are written from here-strings
# in this script, so editing it does change what ships.
$NoBumpPaths = @(
    'docs/*',
    'tests/*',
    '.github/*',
    '*.md',
    'LICENSE*',
    '.gitignore'
)

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

# ---- 1. Preflight -----------------------------------------------------------------------------

if (-not $AudiosurfDir) {
    $AudiosurfDir = 'C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2'
}
$AudiosurfDir = $AudiosurfDir.TrimEnd('\')

# Checked up front and by name. The alternative is an MSBuild reference-resolution error that
# never mentions Audiosurf at all.
$managedDir = Join-Path $AudiosurfDir 'Audiosurf2_Data\Managed'
if (-not (Test-Path $managedDir)) {
    throw @"
Audiosurf 2 not found at:
  $AudiosurfDir

AS2.ModApi compiles against the game's own assemblies, so the game must be installed to build a
release. Point the script at it:

  .\tools\pack.ps1 -AudiosurfDir "D:\SteamLibrary\steamapps\common\Audiosurf 2"
"@
}

# Read off the constant, not the assembly. GenerateAssemblyInfo is off in Directory.Build.props, so
# the DLLs carry no version resource.
# This is only the starting point now. Section 4a decides what the release is actually numbered.
$pluginSource = Get-Content $versionFile -Raw
if ($pluginSource -notmatch 'const\s+string\s+Version\s*=\s*"([^"]+)"') {
    throw 'Could not read ModApiPlugin.Version from src\AS2.ModApi\ModApiPlugin.cs'
}
$version = $Matches[1]

# Before the download, before the build. Publishing rewrites a tracked source file and pushes it,
# and every reason that cannot happen is knowable now. Finding out after BepInEx is staged and two
# assemblies are built is finding out too late.
$upstream = $null
if ($Publish) {
    Write-Step 'Checking the repository can publish'
    $upstream = Assert-PublishReady $repoRoot
    Write-Host "    $($upstream.Slug) via $($upstream.Remote)/$($upstream.Branch)"
}

Write-Step "Packing AS2ModFramework $version"
Write-Host "    game: $AudiosurfDir"

# ---- 2. Acquire BepInEx -----------------------------------------------------------------------

# Before the build, not after. AS2.ModApi references BepInEx.dll and 0Harmony.dll, so staging them
# first builds against the exact BepInEx this release ships rather than whatever is in the game
# folder. On a clean machine there is nothing there at all.

if (-not $BepInExZip) {
    if (-not (Test-Path $cacheDir)) { New-Item -ItemType Directory -Path $cacheDir | Out-Null }
    $BepInExZip = Join-Path $cacheDir "BepInEx_win_x64_$BepInExVersion.zip"

    if (Test-Path $BepInExZip) {
        Write-Step "Using cached BepInEx $BepInExVersion"
    }
    else {
        Write-Step "Downloading BepInEx $BepInExVersion"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $BepInExZip -UseBasicParsing
    }
}

if (-not (Test-Path $BepInExZip)) { throw "BepInEx zip not found: $BepInExZip" }

if (-not $SkipHashCheck) {
    $actual = (Get-FileHash $BepInExZip -Algorithm SHA256).Hash
    if ($actual -ne $BepInExSha256) {
        throw "BepInEx zip hash mismatch.`n  expected $BepInExSha256`n  actual   $actual`nDelete the cached file and retry, or pass -SkipHashCheck if the version was changed deliberately."
    }
}

# ---- 3. Stage BepInEx -------------------------------------------------------------------------

Write-Step 'Staging BepInEx core'

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($BepInExZip)

# An entry name is data from the archive, not a path we chose. "BepInEx/core/../../../evil.dll"
# satisfies the prefix test below, and Join-Path resolves it outside the staging folder. Zip slip.
# The pinned hash makes that unreachable for a normal release build, but -SkipHashCheck and
# -BepInExZip both exist to set that pin aside, so containment is asserted here rather than
# inferred from the hash.
$stageCoreFull = [IO.Path]::GetFullPath($stageCore)
if (-not $stageCoreFull.EndsWith('\')) { $stageCoreFull += '\' }

try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }

        $leaf = Split-Path $entry.FullName -Leaf
        if ($PatchOwnedFiles -contains $leaf) {
            Write-Host "    skipping $($entry.FullName) (belongs to the community patch)"
            continue
        }

        # Everything else outside BepInEx/core is BepInEx's own changelog and similar. Not ours to ship.
        if (-not $entry.FullName.StartsWith('BepInEx/core/')) { continue }

        $target = Join-Path $stageDir ($entry.FullName -replace '/', '\')

        $targetFull = [IO.Path]::GetFullPath($target)
        if (-not $targetFull.StartsWith($stageCoreFull, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing zip entry '$($entry.FullName)': it resolves to $targetFull, outside $stageCoreFull. Is $BepInExZip really the official BepInEx asset?"
        }

        $parent = Split-Path $targetFull -Parent
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $targetFull, $true)
    }
}
finally {
    $archive.Dispose()
}

foreach ($required in @('BepInEx.dll', '0Harmony.dll', 'BepInEx.Preloader.dll')) {
    if (-not (Test-Path (Join-Path $stageCore $required))) {
        throw "BepInEx zip did not contain BepInEx\core\$required -- is $BepInExZip the win_x64 asset?"
    }
}

# ---- 4. Build ---------------------------------------------------------------------------------

# A function because section 4a builds a second time. The version constant is an argument to
# [BepInPlugin], so it is baked into the assembly: the surface has to be read off a build made
# before the bump, and the DLL that ships has to come from one made after it. Two builds of a
# single net35 assembly each cost seconds, and there is no way to have both facts from one.
function Invoke-ProjectBuild {
    # Environment variables rather than -p:. Both values are directory paths that must keep a
    # trailing separator, and a trailing backslash inside a quoted -p: argument escapes the quote.
    # Directory.Build.props guards both with a Condition, so these win.
    $env:AudiosurfDir   = $AudiosurfDir
    $env:BepInExCoreDir = "$stageCore\"

    try {
        foreach ($proj in @('src\AS2.Bootstrap\AS2.Bootstrap.csproj', 'src\AS2.ModApi\AS2.ModApi.csproj')) {
            & dotnet build (Join-Path $repoRoot $proj) -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
        }
    }
    finally {
        Remove-Item Env:\AudiosurfDir   -ErrorAction SilentlyContinue
        Remove-Item Env:\BepInExCoreDir -ErrorAction SilentlyContinue
    }
}

Write-Step 'Building AS2.Bootstrap and AS2.ModApi'
Invoke-ProjectBuild

$bootstrapDll = Join-Path $repoRoot 'build\AS2ModLoader\AS2.Bootstrap.dll'
$modApiDll    = Join-Path $repoRoot 'build\plugins\AS2.ModApi.dll'

foreach ($dll in @($bootstrapDll, $modApiDll)) {
    if (-not (Test-Path $dll)) { throw "Expected build output is missing: $dll" }
}

# ---- 4a. The version ----------------------------------------------------------------------------
#
# What changed since the last release decides the number. tools\Get-NextVersion.ps1 holds the rules
# and the reasoning; this reports the verdict and acts on it.
#
# Only -Publish writes. Packing on its own leaves every tracked file alone and says what a publish
# would have done, which is what this script has always promised.

Write-Step 'Deciding the version'

# Splatted rather than passed straight through. An unsupplied -Bump is an empty string here, and
# an empty string is not in the decider's ValidateSet -- absent has to stay absent.
$override = @{}
if ($Bump)           { $override['Bump'] = $Bump }
if ($ReleaseVersion) { $override['ForceVersion'] = $ReleaseVersion }

$verdict = & (Join-Path $PSScriptRoot 'Get-NextVersion.ps1') `
    -RepoRoot $repoRoot `
    -Assembly $modApiDll `
    -CurrentVersion $version `
    -AssetPattern $assetPattern `
    -AssetEntry $assetEntry `
    -MinorPaths $MinorPaths `
    -NoBumpPaths $NoBumpPaths `
    -AudiosurfDir $AudiosurfDir `
    @override

foreach ($line in $verdict.Reasons) { Write-Host "    $line" }

# Only a publish is refused. Packing is how you get an archive to install and try, and an unchanged
# one is still worth building -- nothing has been claimed to anybody until it is uploaded.
if ($verdict.Level -eq 'none') {
    if (-not $Publish) {
        Write-Host "    nothing that ships changed; packing $version again anyway" -ForegroundColor Yellow
    }
    else {
        throw @"
Nothing that ships has changed since the last release.

The DLL would be identical to the one already published, so a new version number would say a
change happened that did not. Documentation, tests and CI are all outside the archive.

If the release is worth cutting anyway, name the level yourself:

    .\tools\pack.ps1 -Publish -Bump patch
"@
    }
}

Write-Host "    verdict $($verdict.Level): $version -> $($verdict.Next)" -ForegroundColor Green

# Not every publish bumps. A first release and an already-ahead constant both ship the value that
# is on disk, so there is nothing to commit in either case and section 7 has to know that.
$versionChanged = $false

if ($verdict.Next -ne $version) {
    if ($Publish) {
        Write-Step "Taking the version to $($verdict.Next) and rebuilding"
        & (Join-Path $PSScriptRoot 'Set-Version.ps1') -Path $versionFile -Version $verdict.Next | Out-Null
        $version = $verdict.Next
        $versionChanged = $true
        Invoke-ProjectBuild
        foreach ($dll in @($bootstrapDll, $modApiDll)) {
            if (-not (Test-Path $dll)) { throw "Expected build output is missing after the rebuild: $dll" }
        }
    }
    else {
        Write-Host "    packing $version as it stands; -Publish would take it to $($verdict.Next)" -ForegroundColor Yellow
    }
}

# ---- 4b. Invariants -----------------------------------------------------------------------------
#
# README.md tells users this framework cannot change what the game does, and that it reads a score
# and can never set one. Those claims are checked here against the DLL about to be archived, not
# trusted.
# A violation stops the release. Shipping the archive is the moment the claim starts being made to
# somebody.
#
# Mono.Cecil comes from the BepInEx just staged and verified against a pinned hash, so this needs
# nothing installed and nothing downloaded.

Write-Step 'Verifying the read-only and postfix-only invariants'

& (Join-Path $PSScriptRoot 'verify-invariants.ps1') -Assembly $modApiDll -CecilPath (Join-Path $stageCore 'Mono.Cecil.dll')
if ($LASTEXITCODE -ne 0) {
    throw "AS2.ModApi violates an invariant this project publishes. Refusing to package it. See the findings above."
}

New-Item -ItemType Directory -Path (Join-Path $stageDir 'AS2ModLoader')    -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stageDir 'BepInEx\plugins') -Force | Out-Null

Copy-Item $bootstrapDll (Join-Path $stageDir 'AS2ModLoader')    -Force
Copy-Item $modApiDll    (Join-Path $stageDir 'BepInEx\plugins') -Force

# ---- 5. Notices and install instructions ------------------------------------------------------

# The BepInEx zip ships no LICENSE file, so redistributing it means supplying the notice here.
$notices = @"
THIRD-PARTY NOTICES
===================

This package redistributes BepInEx unmodified, taken from the official release:

  BepInEx $BepInExVersion (win_x64)
  $BepInExUrl
  SHA256 $BepInExSha256
  Licensed under the GNU Lesser General Public License v2.1
  https://github.com/BepInEx/BepInEx/blob/master/LICENSE

The files under BepInEx\core\ are byte-for-byte those of that release and can be verified
against it. BepInEx's own winhttp.dll, doorstop_config.ini and .doorstop_version are
deliberately NOT included: Audiosurf 2's community patch owns those files.

BepInEx bundles further components under their own terms, redistributed here unchanged:

  HarmonyX          https://github.com/BepInEx/HarmonyX
  MonoMod           https://github.com/MonoMod/MonoMod
  Mono.Cecil        https://github.com/jbevain/cecil

Refer to each project for the full text of its license.
"@

$readme = @"
AS2ModFramework $version
========================

STEP 1 -- Extract

  Extract this archive into your Audiosurf 2 folder, so that AS2ModLoader\ and BepInEx\ sit
  next to Audiosurf2.exe.

  Not sure where that is? In Steam, right-click Audiosurf 2 > Manage > Browse local files.

STEP 2 -- Add the Steam launch option

  Steam > right-click Audiosurf 2 > Properties > General > Launch Options, and paste:

    --doorstop-target "<your Audiosurf 2 folder>\AS2ModLoader\AS2.Bootstrap.dll"

  Replace <your Audiosurf 2 folder> with the folder from step 1. It must be the full path and
  it must stay in quotes. On a default Steam install that is:

    --doorstop-target "C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2\AS2ModLoader\AS2.Bootstrap.dll"

  A wrong path here does not report an error -- the game just starts unmodded.

STEP 3 -- Launch from Steam

That is the whole install. No file in the game folder is modified; this only adds the two
folders above. The Audiosurf 2 Community Patch keeps working, updater included.

REQUIREMENTS
  - The Audiosurf 2 Community Patch. It supplies the doorstop this attaches to, so without it
    nothing here runs.
  - Do NOT install BepInEx yourself. It is already included, and its installer would replace
    files the community patch owns.

DID IT WORK?
  After a launch, AS2ModLoader\bootstrap.log should end with:
    INFO  Handed off to BepInEx. See BepInEx\LogOutput.log from here on.
  and BepInEx\LogOutput.log should end with "Chainloader startup complete".

UNINSTALLING
  Remove the launch option and delete AS2ModLoader\ and BepInEx\.

Mods go in BepInEx\plugins\.
"@

Set-Content -Path (Join-Path $stageDir 'THIRD-PARTY-NOTICES.txt') -Value $notices -Encoding utf8
Set-Content -Path (Join-Path $stageDir 'README.txt')              -Value $readme  -Encoding utf8

$license = Get-ChildItem $repoRoot -File |
           Where-Object { $_.BaseName -eq 'LICENSE' -or $_.BaseName -eq 'LICENCE' } |
           Select-Object -First 1

if ($license) { Copy-Item $license.FullName $stageDir -Force }
else { Write-Warning 'No LICENSE file in the repo root; the package will ship without one.' }

# ---- 6. Zip and checksum ----------------------------------------------------------------------

# Three archives, from one staging folder.
#
#   AS2ModFramework   everything, the single download for the GitHub release
#   AS2ModLoader      BepInEx core + the bootstrap
#   AS2ModApi         AS2.ModApi.dll alone
#
# The split is not cosmetic. A mod manager deploys a mod by linking files into one directory, so
# AS2ModApi -- a single DLL into BepInEx\plugins\ -- is manageable, while the loader has two install
# roots and needs a Steam launch option no manager can set. Bundled together, every consumer of the
# API inherits the loader's unmanageability. See the Distributing a Mod wiki page.
#
# The combined archive stays, and stays the recommendation on GitHub: that audience is extracting a
# zip by hand and should not have to fetch two. Same files either way, so a user can start with the
# combined download and later update the API alone.

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

# sha256sum format (lowercase hash, two spaces, filename) so it verifies with either
# `sha256sum -c` or Get-FileHash. Published beside the zip so a download can be checked against
# something other than the file it came with.
function New-Package($sourceDir, $name) {
    $zipPath = Join-Path $distDir "$name.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    [IO.Compression.ZipFile]::CreateFromDirectory($sourceDir, $zipPath)

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sha  = "$zipPath.sha256"
    Set-Content -Path $sha -Value "$hash  $name.zip" -Encoding ascii

    Write-Step "Wrote $name.zip"
    # Write-Host, not a bare string. A bare string here goes to the pipeline, which means it joins
    # the object below in what this function returns -- and then $packages[0] is a filename rather
    # than the package, and $packages[0].Zip is nothing at all.
    Get-ChildItem $sourceDir -Recurse -File | ForEach-Object {
        Write-Host ("    " + $_.FullName.Substring($sourceDir.Length + 1))
    }
    Write-Host "    SHA256  $hash" -ForegroundColor Green
    Write-Host ""

    return [pscustomobject]@{ Name = "$name.zip"; Zip = $zipPath; Sha = $sha; Hash = $hash }
}

# Sub-stages, assembled by copying out of the combined one so the two can never disagree about the
# bytes they ship.
$loaderStage = Join-Path $repoRoot 'build\stage-loader'
$apiStage    = Join-Path $repoRoot 'build\stage-api'

foreach ($d in @($loaderStage, $apiStage)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d | Out-Null
}

# Loader: BepInEx core and the bootstrap. Carries the third-party notices, because it is the package
# that actually contains BepInEx and LGPL requires the notice to travel with the files.
New-Item -ItemType Directory -Path (Join-Path $loaderStage 'BepInEx') -Force | Out-Null
Copy-Item (Join-Path $stageDir 'BepInEx\core')  (Join-Path $loaderStage 'BepInEx\core') -Recurse -Force
Copy-Item (Join-Path $stageDir 'AS2ModLoader')  $loaderStage -Recurse -Force
foreach ($f in @('LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'README.txt')) {
    $src = Join-Path $stageDir $f
    if (Test-Path $src) { Copy-Item $src $loaderStage -Force }
}

# API: one DLL. No BepInEx here, so no third-party notice is owed.
New-Item -ItemType Directory -Path (Join-Path $apiStage 'BepInEx\plugins') -Force | Out-Null
Copy-Item $modApiDll (Join-Path $apiStage 'BepInEx\plugins') -Force
if (Test-Path (Join-Path $stageDir 'LICENSE.txt')) {
    Copy-Item (Join-Path $stageDir 'LICENSE.txt') $apiStage -Force
}

$apiReadme = @"
AS2ModApi $version
==================

The shared mod API for Audiosurf 2: the events, UI helpers and Mod Menu that mods build on.

This package is ONLY the API. It does nothing on its own and will not load without the mod loader.

REQUIRES
  1. The Audiosurf 2 Community Patch.
  2. AS2ModLoader $version, which bundles BepInEx and includes the Steam launch option step.
     Install that first; if the game is not already loading mods, this will not change that.

INSTALLING
  Extract into your Audiosurf 2 folder, so that BepInEx\plugins\AS2.ModApi.dll lands next to your
  other plugins. There is no launch option to set for this package; the loader owns that.

DID IT WORK?
  BepInEx\LogOutput.log should contain:
    [Info   :Audiosurf 2 Mod API] Mod API ready. Game root: ...

UNINSTALLING
  Delete BepInEx\plugins\AS2.ModApi.dll. Mods that depend on it will stop loading.
"@
Set-Content -Path (Join-Path $apiStage 'README.txt') -Value $apiReadme -Encoding utf8

$packages = @(
    (New-Package $stageDir    "AS2ModFramework-$version"),
    (New-Package $loaderStage "AS2ModLoader-$version"),
    (New-Package $apiStage    "AS2ModApi-$version")
)

# Named for the release notes and the -Publish step below.
$outputZip = $packages[0].Zip
$shaFile   = $packages[0].Sha
$zipName   = $packages[0].Name
$zipHash   = $packages[0].Hash

# ---- 7. Publish -------------------------------------------------------------------------------

$tag = "v$version"

# Every zip and every checksum, so a release carries the combined download and both split packages.
$assets = @()
foreach ($p in $packages) { $assets += $p.Zip; $assets += $p.Sha }

if (-not $Publish) {
    Write-Host ""
    Write-Step 'Not published. To publish this release:'
    Write-Host "    .\tools\pack.ps1 -Publish"
    Write-Host "  or upload these to a new '$tag' release by hand:"
    foreach ($p in $packages) { Write-Host "       $($p.Name)  +  $($p.Name).sha256" }
    return
}

# The bump reaches the remote before the tag does. gh release create tags whatever the default
# branch points at, so a bump that is written but not pushed makes a tag whose source still says
# the previous version -- which is exactly the agreement between a filename and a log line that
# reading the version off a constant exists to keep.
if ($versionChanged) {
    Write-Step "Committing and pushing the version bump"
    Save-VersionBump $repoRoot $versionFile $version $upstream
    Write-Host "    pushed to $($upstream.Remote)/$($upstream.Branch)"
}

Write-Step "Creating release $tag"

$checksums = ($packages | ForEach-Object { "    $($_.Hash)  $($_.Name)" }) -join "`n"

$notes = @"
**Most people want ``$zipName``** -- extract it into your Audiosurf 2 folder and add the Steam
launch option. See README.txt in the archive, or the install section of the repo README.

Requires the Audiosurf 2 Community Patch. BepInEx $BepInExVersion is bundled; do not install it
separately.

### The split packages

Same files, for anyone who wants the pieces separately:

- ``AS2ModLoader-$version.zip`` -- BepInEx and the bootstrap. Required by every mod, and the package
  that needs the Steam launch option.
- ``AS2ModApi-$version.zip`` -- the shared mod API alone, a single DLL into ``BepInEx\plugins\``.
  Update this on its own when events change, without re-downloading BepInEx. Needs the loader.

Verify a download before extracting:

    Get-FileHash .\$zipName -Algorithm SHA256

$checksums
"@

# --repo, because the remote is not assumed to be called origin. The slug came off the tracked
# upstream in the preflight.
& gh release create $tag @assets --repo $($upstream.Slug) --title "AS2ModFramework $version" --notes $notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $tag" }

Write-Step "Published $tag"
