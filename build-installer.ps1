# Compila FanMaxxxing y genera el instalador MSI en .\dist
# Uso:  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'

Write-Host '1/3  Publicando la app (autocontenida, un solo .exe)…' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\FanMaxxxing\FanMaxxxing.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de la app' }

Write-Host '2/3  Generando el MSI (WiX 5)…' -ForegroundColor Cyan
dotnet build (Join-Path $root 'installer\FanMaxxxing.Installer.wixproj') -c Release "-p:AppPublishDir=$publish"
if ($LASTEXITCODE -ne 0) { throw 'Falló la generación del MSI' }

Write-Host '3/3  Copiando a dist…' -ForegroundColor Cyan
$version = (Get-Item (Join-Path $publish 'FanMaxxxing.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$msi = Get-ChildItem (Join-Path $root 'installer\bin\Release') -Recurse -Filter 'FanMaxxxing-Setup.msi' | Select-Object -First 1
New-Item -ItemType Directory -Force $dist | Out-Null
$target = Join-Path $dist "FanMaxxxing-$version-Setup.msi"
Copy-Item $msi.FullName $target -Force
Write-Host ("Listo: {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB)) -ForegroundColor Green
