<#
.SYNOPSIS
    Writing a release archive, and the layout rules every package has to satisfy. Dot-sourced by
    tools\pack.ps1 and by tests\Check-ArchiveLayout.ps1.

.DESCRIPTION
    This used to be four lines inside pack.ps1's New-Package. It is a file of its own because the
    rules below are now a published contract, and a contract nothing checks is a contract that
    drifts.

    The archives are extracted by Vortex as well as by a person with File Explorer, and a mod
    manager is stricter than Explorer is. It matches entry names against its own installer rules,
    it links every file into the game folder rather than copying them, and it reports a conflict
    when two managed mods claim the same path. Every rule in Test-ArchiveLayout is here because of
    something that broke under one of those three, or would have.

    Splitting it out is also what lets CI check any of it. Nothing here needs Audiosurf 2, BepInEx
    or a build, so tests\Check-ArchiveLayout.ps1 drives these functions cold against temporary
    folders on a runner that has none of the above.
#>

# No Set-StrictMode here. This file is dot-sourced, so it would land in pack.ps1's scope and change
# how that script treats every unset property it has ever had. Same reasoning as ReleaseGit.ps1,
# and the same reason that file says so too.

# ---- Writing ----------------------------------------------------------------------------------

<#
.SYNOPSIS
    Zip a folder, writing entry names the way the zip format says to
#>
function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$ZipPath
    )

    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Not ZipFile::CreateFromDirectory, which is what this was.
    #
    # Under Windows PowerShell 5.1 that API takes the platform separator and writes entry names as
    # BepInEx\core\0Harmony.dll. APPNOTE 4.4.17.1 says the separator is a forward slash and a
    # backslash is a literal character in a file name, so those archives are malformed. Explorer
    # and 7-Zip both recover from it, which is why it survived three releases unnoticed. An
    # extractor that reads the spec instead produces one file named "BepInEx\core\0Harmony.dll"
    # sitting in the root, and then nothing loads and nothing says why.
    #
    # Sorting is not cosmetic either. Get-ChildItem returns directory order, so two packs of the
    # same tree could differ in entry order and therefore in bytes, for no reason a reader of the
    # published checksum could ever explain.
    $root = (Resolve-Path $SourceDir).ProviderPath.TrimEnd('\')

    $zip = [IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        Get-ChildItem $root -Recurse -File | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
}

<#
.SYNOPSIS
    Every entry name in an archive, as the archive itself spells them
#>
function Get-ZipEntryName {
    param([Parameter(Mandatory)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        # Read the names out inside the try. The entry collection is a view over an open handle, so
        # returning it hands the caller something that is empty by the time anything reads it.
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally { $zip.Dispose() }

    return ,$names
}

# ---- The layout contract ----------------------------------------------------------------------

<#
.SYNOPSIS
    The entries an installer recognises a package by
#>
# A Vortex extension decides whether a downloaded archive belongs to this game by looking inside it
# for these names. That extension lives in a different repository, so moving either file here stops
# every archive being recognised and nothing in this repo reports it.
#
# Treat them as published. Renaming one is a coordinated change across two repositories and every
# copy already installed, not a tidy-up.
function Get-RequiredEntry {
    param([Parameter(Mandatory)][string]$Package)

    switch -Exact ($Package) {
        'AS2ModFramework' { return ,@('AS2ModLoader/AS2.Bootstrap.dll', 'BepInEx/plugins/AS2.ModApi.dll') }
        'AS2ModLoader'    { return ,@('AS2ModLoader/AS2.Bootstrap.dll') }
        'AS2ModApi'       { return ,@('BepInEx/plugins/AS2.ModApi.dll') }
    }

    throw "No archive layout is declared for a package named '$Package'. Add one to Get-RequiredEntry."
}

<#
.SYNOPSIS
    Check an entry list against the rules, returning one complaint per broken rule
#>
function Test-ArchiveLayout {
    param(
        [string[]]$Entries = @(),
        [string[]]$RequiredEntries = @(),
        [string[]]$ForbiddenLeafName = @()
    )

    $complaints = New-Object System.Collections.Generic.List[string]

    foreach ($entry in $Entries) {

        # Rule 1. The separator. See the comment in New-ZipFromDirectory for what a conforming
        # extractor does with the other one.
        if ($entry.Contains('\')) {
            $complaints.Add("uses a backslash separator: $entry")
        }

        # Rule 2. Nothing at the archive root.
        #
        # Vortex links a managed mod's files into the game folder and merges the mods that share
        # it. Two packages that both carry README.txt at the root therefore claim the same path in
        # the game folder, and a user holding the loader and the API gets a file conflict over a
        # text file. Every file belongs under a folder its own package owns.
        if (-not $entry.Contains('/')) {
            $complaints.Add("sits at the archive root: $entry")
        }

        # Rule 3. Nothing that escapes the folder it is extracted into.
        #
        # Nothing in this repo could produce one of these, which is the point of checking. The day
        # something does, it is a path traversal inside an archive people are told to trust, and it
        # should fail on the machine that built it rather than on theirs.
        if ($entry.StartsWith('/') -or $entry -match '^[A-Za-z]:' -or $entry -match '(^|/)\.\.(/|$)') {
            $complaints.Add("escapes the extraction folder: $entry")
        }

        # Rule 4. None of the files the community patch owns.
        #
        # pack.ps1 already drops these while it unpacks BepInEx, so this is the same rule asserted
        # where it actually matters: on the bytes about to be uploaded, rather than on the loop that
        # was supposed to filter them. Shipping winhttp.dll replaces the patch's UnityDoorstop 3.4.1
        # with 4.5.0, which resolves a different entry point, and breaks both projects at once on
        # somebody else's machine.
        $leaf = $entry.Substring($entry.LastIndexOf('/') + 1)
        foreach ($forbidden in $ForbiddenLeafName) {
            if ($leaf -eq $forbidden) {
                $complaints.Add("ships a file the community patch owns: $entry")
            }
        }
    }

    # Rule 5. The markers are present, spelled exactly as declared.
    foreach ($required in $RequiredEntries) {
        if ($Entries -ccontains $required) { continue }

        # A case-only miss earns its own line. BepInEx/plugins and bepinex/plugins are one folder to
        # Windows and two different strings to an installer matching entry names, so the archive
        # looks correct in Explorer and is not recognised.
        $nearMiss = @($Entries | Where-Object { $_ -eq $required })
        if ($nearMiss.Count -gt 0) {
            $complaints.Add("marker differs only in case: expected $required, found $($nearMiss[0])")
        }
        else {
            $complaints.Add("marker is missing: $required")
        }
    }

    # The comma keeps an empty list an empty array. Without it PowerShell unrolls the return value,
    # a caller reading .Count on a clean archive gets nothing at all, and the check that was meant
    # to pass throws instead.
    return ,$complaints.ToArray()
}
