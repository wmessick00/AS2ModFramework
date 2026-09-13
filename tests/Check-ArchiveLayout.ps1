<#
.SYNOPSIS
    Cold checks for the archive writing and the layout rules in tools\PackArchive.ps1.

.DESCRIPTION
    Three releases shipped with backslash entry separators and nobody noticed, because Explorer and
    7-Zip both recover from a malformed archive and a person extracting one by hand never sees the
    entry names. A mod manager does see them. So the layout is a contract now, and this is what
    keeps it.

    The marker entries are the sharper half. A Vortex extension in a different repository decides
    whether a download belongs to this game by looking for AS2ModLoader/AS2.Bootstrap.dll and
    BepInEx/plugins/AS2.ModApi.dll. Move either one here and every archive stops being recognised,
    with nothing in either repository to report it. That is the failure this file exists to make
    loud.

    No test framework, matching tests\Check.cs and tests\Check-ReleaseTooling.ps1, for the reason
    those give: this repository takes no dependency it does not already have.

    Dot-sourced rather than parsed. Check-ReleaseTooling.ps1 lifts its functions out of
    Get-NextVersion.ps1 by parsing, because that script has a param block and top-level code that
    would run. PackArchive.ps1 is functions and nothing else, which is most of why it is a separate
    file.

    Nothing here needs Audiosurf 2, BepInEx or a build. It writes real zips into the temp folder
    and reads them back, so it runs on a CI runner that owns none of the above.

.EXAMPLE
    .\tests\Check-ArchiveLayout.ps1
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

# Every rule in Test-ArchiveLayout has to be able to fail, or it proves nothing. This asserts the
# complaint that a broken rule produces, rather than only that a clean list produces none.
function Complains($what, $entries, $fragment) {
    $complaints = Test-ArchiveLayout -Entries $entries -ForbiddenLeafName @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
    $hit = @($complaints | Where-Object { $_ -like "*$fragment*" })
    if ($hit.Count -gt 0) { Pass $what }
    else { Fail "$what -- no complaint matched '*$fragment*'; got: $($complaints -join ' | ')" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$module = Join-Path $repoRoot 'tools\PackArchive.ps1'

if (-not (Test-Path $module)) { throw "Not found: $module" }
. $module

Write-Output "Dot-sourced tools\PackArchive.ps1"
Write-Output ''

# ---- A clean archive ---------------------------------------------------------------------------

Write-Output 'Reading a well-formed entry list'

$clean = @(
    'AS2ModLoader/AS2.Bootstrap.dll',
    'AS2ModLoader/README.txt',
    'BepInEx/core/0Harmony.dll',
    'BepInEx/plugins/AS2.ModApi.dll'
)

Same 'a conforming archive draws no complaint' `
     (Test-ArchiveLayout -Entries $clean -RequiredEntries (Get-RequiredEntry 'AS2ModFramework')).Count 0

# The empty case is the one PowerShell gets wrong on its own. An unrolled empty return reads as
# $null, .Count throws under StrictMode, and the check that was supposed to pass takes the build
# down instead.
Same 'an empty archive still returns a countable result' (Test-ArchiveLayout -Entries @()).Count 0

# ---- Each rule, broken on purpose --------------------------------------------------------------

Write-Output ''
Write-Output 'Breaking each rule once'

Complains 'a backslash separator is caught' @('BepInEx\core\0Harmony.dll') 'backslash'
Complains 'a file at the archive root is caught' @('README.txt') 'archive root'
Complains 'a parent traversal is caught' @('../../Windows/System32/evil.dll') 'escapes'
Complains 'a rooted path is caught' @('/etc/passwd') 'escapes'
Complains 'a drive letter is caught' @('C:/Windows/System32/evil.dll') 'escapes'

# The three the community patch owns. Shipping any of them replaces its UnityDoorstop 3.4.1 with
# 4.5.0, which resolves a different entry point, and breaks both projects at once.
Complains 'winhttp.dll is refused' @('winhttp.dll') 'community patch owns'
Complains 'doorstop_config.ini is refused' @('doorstop_config.ini') 'community patch owns'
Complains 'a patch-owned file is refused from a subfolder too' @('BepInEx/core/winhttp.dll') 'community patch owns'

$missing = Test-ArchiveLayout -Entries @('BepInEx/plugins/AS2.ModApi.dll') -RequiredEntries (Get-RequiredEntry 'AS2ModFramework')
True 'a missing marker is caught' (@($missing | Where-Object { $_ -like '*marker is missing*' }).Count -eq 1)

# Windows calls these one folder. An installer matching entry names calls them two strings, so the
# archive looks right in Explorer and is not recognised.
$wrongCase = Test-ArchiveLayout -Entries @('bepinex/plugins/as2.modapi.dll') -RequiredEntries (Get-RequiredEntry 'AS2ModApi')
True 'a marker that differs only in case is caught' (@($wrongCase | Where-Object { $_ -like '*only in case*' }).Count -eq 1)
True 'and it is not reported as missing' (@($wrongCase | Where-Object { $_ -like '*is missing*' }).Count -eq 0)

# ---- The declared markers ----------------------------------------------------------------------

Write-Output ''
Write-Output 'Declaring what each package must contain'

True 'the combined package carries both markers' `
     ((Get-RequiredEntry 'AS2ModFramework').Count -eq 2)
Same 'the loader package carries the bootstrap' `
     (Get-RequiredEntry 'AS2ModLoader')[0] 'AS2ModLoader/AS2.Bootstrap.dll'
Same 'the API package carries the API' `
     (Get-RequiredEntry 'AS2ModApi')[0] 'BepInEx/plugins/AS2.ModApi.dll'

# The combined package spells both marker strings out a second time, so renaming one in the loader
# or API arm and missing the copy leaves the three arms disagreeing about what a marker is. Found
# by moving the API marker and watching only two of the three checks above notice.
$split = @((Get-RequiredEntry 'AS2ModLoader') + (Get-RequiredEntry 'AS2ModApi') | Sort-Object)
$combined = @((Get-RequiredEntry 'AS2ModFramework') | Sort-Object)
True 'the combined package is exactly the two split packages' `
     (@(Compare-Object $split $combined -CaseSensitive).Count -eq 0)

# A fourth package added to pack.ps1 without a layout declared here would otherwise be checked
# against nothing and pass.
Throws 'a package with no declared layout is refused' { Get-RequiredEntry 'AS2ModSomethingElse' }

# ---- Writing a real archive --------------------------------------------------------------------

Write-Output ''
Write-Output 'Writing and reading a zip'

$temp = Join-Path ([IO.Path]::GetTempPath()) ("as2-archive-check-" + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $temp 'stage'
$zipPath = Join-Path $temp 'out.zip'

try {
    New-Item -ItemType Directory -Path (Join-Path $source 'AS2ModLoader') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'BepInEx\core') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'BepInEx\plugins') -Force | Out-Null

    Set-Content -Path (Join-Path $source 'AS2ModLoader\AS2.Bootstrap.dll') -Value 'not a dll' -Encoding ascii
    Set-Content -Path (Join-Path $source 'AS2ModLoader\README.txt')        -Value 'read me'   -Encoding ascii
    Set-Content -Path (Join-Path $source 'BepInEx\core\0Harmony.dll')      -Value 'not a dll' -Encoding ascii
    Set-Content -Path (Join-Path $source 'BepInEx\plugins\AS2.ModApi.dll') -Value 'not a dll' -Encoding ascii

    New-ZipFromDirectory -SourceDir $source -ZipPath $zipPath
    $entries = Get-ZipEntryName -ZipPath $zipPath

    Same 'every file reaches the archive' $entries.Count 4
    Same 'no entry carries a backslash' (@($entries | Where-Object { $_.Contains('\') }).Count) 0
    True 'the nested entry is spelled with forward slashes' ($entries -ccontains 'BepInEx/core/0Harmony.dll')

    Same 'the written archive satisfies its own rules' `
         (Test-ArchiveLayout -Entries $entries -RequiredEntries (Get-RequiredEntry 'AS2ModFramework')).Count 0

    # Directory order is not guaranteed, so without the sort two packs of one tree can differ in
    # bytes and the published checksum changes for no reason anybody can explain.
    $sorted = @($entries | Sort-Object)
    True 'entries come out in a stable order' (@(Compare-Object $entries $sorted -SyncWindow 0).Count -eq 0)

    # Writing over an existing file has to replace it. ZipFile::Open in Create mode on a path that
    # already holds a zip appends, and the second pack of a release would carry both.
    New-ZipFromDirectory -SourceDir $source -ZipPath $zipPath
    Same 'a second write replaces the archive rather than appending' (Get-ZipEntryName -ZipPath $zipPath).Count 4

    # ---- The regression this file was written for ----------------------------------------------

    Write-Output ''
    Write-Output 'Reproducing the bug that shipped'

    # This is what New-ZipFromDirectory replaced. Under Windows PowerShell 5.1 it writes the
    # platform separator, which is how 0.1.0, 0.2.1 and 0.2.2 all went out malformed.
    #
    # Kept as a check rather than as a comment because it is the one way to prove the rule catches
    # the real thing. If a future .NET makes CreateFromDirectory conform, this fails and says so,
    # and the answer then is to delete this block -- not to loosen the rule.
    $legacyZip = Join-Path $temp 'legacy.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $legacyZip)
    $legacyEntries = Get-ZipEntryName -ZipPath $legacyZip

    True 'CreateFromDirectory still writes backslashes here' `
         (@($legacyEntries | Where-Object { $_.Contains('\') }).Count -gt 0)

    $caught = Test-ArchiveLayout -Entries $legacyEntries -RequiredEntries (Get-RequiredEntry 'AS2ModFramework')
    True 'and the rules reject that archive' ($caught.Count -gt 0)
    True 'naming the separator as the reason' (@($caught | Where-Object { $_ -like '*backslash*' }).Count -gt 0)
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
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
