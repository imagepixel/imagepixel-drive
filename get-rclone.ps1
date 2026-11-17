<#
.SYNOPSIS
    Fetches rclone for Windows and puts it into bin\rclone.exe.

.DESCRIPTION
    Imagepixel Drive uses rclone as it is released, without changes. This script
    downloads the pinned release from the rclone project, compares the file
    with the pinned SHA-256 checksum and unpacks rclone.exe.

    To move to a newer rclone, change the version and take the checksum from
    the SHA256SUMS file of that release.
#>
[CmdletBinding()]
param(
    [string]$RcloneVersion = 'v1.76.0',
    [string]$Sha256 = '8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc',
    [string]$Output = (Join-Path $PSScriptRoot 'bin\rclone.exe')
)

$ErrorActionPreference = 'Stop'

$name = "rclone-$RcloneVersion-windows-amd64"
$folder = Join-Path $PSScriptRoot ".build\rclone-$RcloneVersion"
$archive = Join-Path $folder "$name.zip"

if (-not (Test-Path $folder)) {
    New-Item -ItemType Directory -Path $folder | Out-Null
}

if (-not (Test-Path $archive)) {
    Write-Host "Fetching rclone $RcloneVersion ..."
    Invoke-WebRequest -UseBasicParsing -OutFile $archive `
        -Uri "https://github.com/rclone/rclone/releases/download/$RcloneVersion/$name.zip"
}

$actual = (Get-FileHash -Algorithm SHA256 -Path $archive).Hash.ToLowerInvariant()
if ($actual -ne $Sha256.ToLowerInvariant()) {
    Remove-Item $archive
    throw "The checksum of $name.zip is $actual, expected was $Sha256. The file was removed."
}

Expand-Archive -Path $archive -DestinationPath $folder -Force

$outputDir = Split-Path -Parent $Output
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Copy-Item -Path (Join-Path $folder "$name\rclone.exe") -Destination $Output -Force
Write-Host "Done: $Output"
