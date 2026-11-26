# Builds FanMaxxxing and generates the MSI installers (English and Spanish) in .\dist
# Usage:  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'

Write-Host '1/3  Publishing the app (self-contained, single .exe)...' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\FanMaxxxing\FanMaxxxing.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }

Write-Host '2/3  Building the MSIs (WiX 5)...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'installer\FanMaxxxing.Installer.wixproj') -c Release "-p:AppPublishDir=$publish"
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }

Write-Host '3/3  Copying to dist...' -ForegroundColor Cyan
$version = (Get-Item (Join-Path $publish 'FanMaxxxing.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
New-Item -ItemType Directory -Force $dist | Out-Null
# The update checker picks the asset ending in -<language>.msi (falls back to -en.msi)
$cultures = @{ 'en-US' = 'en'; 'es-ES' = 'es' }
foreach ($c in $cultures.Keys) {
    $msi = Get-ChildItem (Join-Path $root "installer\bin\Release\$c") -Recurse -Filter 'FanMaxxxing-Setup.msi' | Select-Object -First 1
    if (-not $msi) { throw "MSI for $c not found" }
    $target = Join-Path $dist "FanMaxxxing-$version-Setup-$($cultures[$c]).msi"
    Copy-Item $msi.FullName $target -Force
    Write-Host ("Done: {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB)) -ForegroundColor Green
}
