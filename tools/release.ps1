#Requires -Version 5.1
<#
.SYNOPSIS
    Build and package a directly downloadable TaskbarDrawer zip.

.DESCRIPTION
    Reads the version from TaskbarDrawer.csproj, runs build + tests, publishes the
    framework-dependent single-file exe, writes 使用说明.txt from the template,
    copies LICENSE, and creates dist/TaskbarDrawer-<version>-win-x64.zip.

    The package is intentionally framework-dependent: it needs .NET 8 Desktop Runtime.

.EXAMPLE
    pwsh tools/release.ps1
.EXAMPLE
    powershell -NoProfile -File tools/release.ps1 -AllowDirty
#>
param(
    [string]$Version = '',
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo 'src/TaskbarDrawer/TaskbarDrawer.csproj'
$template = Join-Path $PSScriptRoot '使用说明.template.txt'
$license = Join-Path $repo 'LICENSE'

if (-not (Test-Path -LiteralPath $csproj)) { throw "csproj not found: $csproj" }
if (-not (Test-Path -LiteralPath $template)) { throw "template not found: $template" }
if (-not (Test-Path -LiteralPath $license)) { throw "LICENSE not found: $license" }

if (-not $Version) {
    $match = [regex]::Match((Get-Content -LiteralPath $csproj -Raw), '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw "cannot read <Version> from $csproj" }
    $Version = $match.Groups[1].Value.Trim()
}

$dirty = (git -C $repo status --porcelain)
if ($dirty -and -not $AllowDirty) {
    throw "working tree is dirty; commit first or pass -AllowDirty"
}

$distRoot = Join-Path $repo 'dist'
$packageName = "TaskbarDrawer-$Version-win-x64"
$packageDir = Join-Path $distRoot $packageName
$zipPath = Join-Path $distRoot "$packageName.zip"
if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Write-Host "== TaskbarDrawer $Version =="
Write-Host "build"
dotnet build (Join-Path $repo 'src/TaskbarDrawer/TaskbarDrawer.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "test"
dotnet test (Join-Path $repo 'tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "test failed" }

Write-Host "publish"
dotnet publish (Join-Path $repo 'src/TaskbarDrawer/TaskbarDrawer.csproj') -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -o $packageDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $packageDir 'TaskbarDrawer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "published exe not found" }
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if (-not $fileVersion.StartsWith($Version)) {
    throw "exe FileVersion $fileVersion does not match $Version"
}
# Keep the download lean: the previous release shipped only exe + usage.
Get-ChildItem -LiteralPath $packageDir -Filter '*.pdb' -File | Remove-Item -Force

$text = Get-Content -LiteralPath $template -Raw -Encoding UTF8
$text = $text.Replace('{{VERSION}}', $Version)
$text = $text.Replace('{{RELEASE_URL}}', "https://github.com/Simiely/taskbar-drawer/releases/tag/v$Version")
$text = $text -replace "`r?`n", "`r`n"
$usage = Join-Path $packageDir '使用说明.txt'
[System.IO.File]::WriteAllText($usage, $text, (New-Object System.Text.UTF8Encoding($true)))
Copy-Item -LiteralPath $license -Destination (Join-Path $packageDir 'LICENSE.txt') -Force

Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "package: $zipPath"
Write-Host ("sha256:  " + $hash)
Write-Host ("size:    " + (Get-Item -LiteralPath $zipPath).Length + " bytes")
Write-Host ("exe:     " + $fileVersion)
