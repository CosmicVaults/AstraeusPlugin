<#
    Checks a N.I.N.A. plugin manifest made by CreateManifest.ps1 before it is released.

    The manifest repository's schema only requires a few fields. This also fails on any field we
    mean to fill that came out empty, on tags with stray spaces (CreateManifest.ps1 splits the Tags
    metadata on commas without trimming), and on a channel, version or N.I.N.A. minimum that isn't
    the one intended. Every problem is listed, then the script exits 1 if there were any.

    Runs under Windows PowerShell 5.1 and PowerShell 7:

        .\.github\scripts\check-manifest.ps1 -Path manifest.json -Channel beta -Version 1.0.1.278
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [ValidateSet('beta', 'release')] [string] $Channel,
    [string] $Version,
    # Also the folder the submit workflow files the manifest under. Change both together.
    [string] $MinimumApplicationVersion = '3.2.0.9001'
)

$ErrorActionPreference = 'Stop'
$problems = New-Object System.Collections.Generic.List[string]
$manifest = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json

function Get-Field($object, [string] $dottedName) {
    $value = $object
    foreach ($part in $dottedName.Split('.')) {
        if ($null -eq $value) { return $null }
        $value = $value.$part
    }
    return $value
}

function Test-Text([string] $dottedName, [int] $maxLength) {
    $value = Get-Field $manifest $dottedName
    if ($null -eq $value -or "$value".Trim() -eq '') {
        $problems.Add("$dottedName is missing or empty.")
    } elseif ("$value".Length -gt $maxLength) {
        $problems.Add("$dottedName is $("$value".Length) characters, over the limit of $maxLength.")
    }
}

function Get-FourPartVersion([string] $dottedName) {
    $value = Get-Field $manifest $dottedName
    $parts = @()
    foreach ($name in 'Major', 'Minor', 'Patch', 'Build') {
        $part = if ($null -ne $value) { $value.$name } else { $null }
        if ("$part" -notmatch '^\d+$') {
            $problems.Add("$dottedName.$name is '$part', not a number.")
        }
        $parts += "$part"
    }
    return ($parts -join '.')
}

# Text fields, with the schema's length limits where it has them.
Test-Text 'Name' 50
Test-Text 'Identifier' 36
Test-Text 'Author' 256
Test-Text 'Homepage' 2048
Test-Text 'Repository' 2048
Test-Text 'License' 512
Test-Text 'LicenseURL' 2048
Test-Text 'ChangelogURL' 2048
Test-Text 'Descriptions.ShortDescription' 256
Test-Text 'Descriptions.LongDescription' 10000
Test-Text 'Descriptions.FeaturedImageURL' 2048
Test-Text 'Descriptions.ScreenshotURL' 2048
Test-Text 'Descriptions.AltScreenshotURL' 2048
Test-Text 'Installer.URL' 2048

if ("$($manifest.Identifier)" -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
    $problems.Add("Identifier '$($manifest.Identifier)' is not a GUID.")
}

$fileVersion = Get-FourPartVersion 'Version'
if ($Version -and $fileVersion -ne $Version) {
    $problems.Add("Version is $fileVersion, not $Version.")
}
$minimum = Get-FourPartVersion 'MinimumApplicationVersion'
if ($minimum -ne $MinimumApplicationVersion) {
    $problems.Add("MinimumApplicationVersion is $minimum, not $MinimumApplicationVersion.")
}

$tags = @($manifest.Tags | Where-Object { $null -ne $_ })
if ($tags.Count -eq 0) {
    $problems.Add('Tags is missing or empty.')
} elseif ($tags.Count -gt 20) {
    $problems.Add("Tags has $($tags.Count) entries, over the limit of 20.")
}
foreach ($tag in $tags) {
    if ("$tag".Trim() -ne "$tag") { $problems.Add("Tag '$tag' has leading or trailing spaces.") }
    if ("$tag".Trim().Length -lt 1 -or "$tag".Length -gt 30) { $problems.Add("Tag '$tag' is not 1 to 30 characters.") }
}

if ($manifest.Installer.Type -ne 'ARCHIVE') {
    $problems.Add("Installer.Type is '$($manifest.Installer.Type)', not ARCHIVE.")
}
if ($manifest.Installer.ChecksumType -ne 'SHA256') {
    $problems.Add("Installer.ChecksumType is '$($manifest.Installer.ChecksumType)', not SHA256.")
}
if ("$($manifest.Installer.Checksum)" -notmatch '^[0-9a-fA-F]{64}$') {
    $problems.Add("Installer.Checksum '$($manifest.Installer.Checksum)' is not a SHA-256.")
}

$channelValue = $manifest.Channel
if ($Channel -eq 'beta' -and $channelValue -ne 'Beta') {
    $problems.Add("Channel is '$channelValue', but a beta release needs 'Beta'.")
}
if ($Channel -eq 'release' -and $null -ne $channelValue) {
    $problems.Add("Channel is '$channelValue', but a full release must not have one.")
}

if ($problems.Count -gt 0) {
    Write-Host "The manifest $Path has $($problems.Count) problem(s):"
    foreach ($problem in $problems) { Write-Host "  - $problem" }
    exit 1
}
Write-Host "The manifest $Path is complete: version $fileVersion, N.I.N.A. $minimum or later, channel $Channel, $($tags.Count) tags."
