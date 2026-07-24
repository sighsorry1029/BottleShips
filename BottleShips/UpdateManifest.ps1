param (
    [Parameter(Mandatory = $true)]
    [string] $manifestFile,

    [Parameter(Mandatory = $true)]
    [string] $versionString
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
    throw "Manifest file not found: $manifestFile"
}

$manifest = [System.IO.File]::ReadAllText($manifestFile)
$versionPattern = '"version_number":\s*"[^"]*"'
if (-not [System.Text.RegularExpressions.Regex]::IsMatch($manifest, $versionPattern)) {
    throw "version_number was not found in manifest: $manifestFile"
}

$replacement = '"version_number": "' + $versionString + '"'
$versionRegex = [System.Text.RegularExpressions.Regex]::new($versionPattern)
$updatedManifest = $versionRegex.Replace($manifest, $replacement, 1)

$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestFile, $updatedManifest, $utf8WithoutBom)
