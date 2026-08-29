<#
.SYNOPSIS
    Writes a new value into the Version constant that tools\pack.ps1 reads.

.DESCRIPTION
    One const in one source file is the single source of truth for a mod's version. BepInEx prints
    it in LogOutput.log because it is an argument to [BepInPlugin], and pack.ps1 names the archive
    and the tag from it, so a downloaded file name and a user's log line always agree.

    That means the constant has to be edited in place rather than generated, and it means the edit
    has to be exact. This does the substitution and refuses anything ambiguous: no match, or more
    than one, throws rather than guessing which one was meant.

    The file is read and written as UTF-8 with no byte order mark, and the substitution runs against
    the whole file as one string, so CRLF line endings survive untouched. All three repos keep their
    sources that way.

.PARAMETER Path
    The .cs file holding the constant.

.PARAMETER Version
    The new value. Three numbers.

.EXAMPLE
    .\tools\Set-Version.ps1 -Path src\AS2.ModApi\ModApiPlugin.cs -Version 0.3.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) { throw "Version source not found: $Path" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Not a three-part version: '$Version'" }

# The same shape pack.ps1 has always grepped for, kept in step deliberately. If this pattern and
# the one that reads it ever drift, the release names itself one thing and reports another.
$pattern = '(const\s+string\s+Version\s*=\s*")([^"]+)(")'

$encoding = New-Object System.Text.UTF8Encoding($false)
$text = [IO.File]::ReadAllText($Path, $encoding)

$found = [regex]::Matches($text, $pattern)
if ($found.Count -ne 1) {
    throw "Expected exactly one 'const string Version' in $Path, found $($found.Count). Refusing to guess which one to write."
}

$old = $found[0].Groups[2].Value
if ($old -eq $Version) { return $old }

$updated = [regex]::Replace($text, $pattern, "`${1}$Version`${3}")
[IO.File]::WriteAllText($Path, $updated, $encoding)

return $old
