# Builds publish\DualSenseTweaker-<version>-x64.msi: publishes the self-contained app as a folder
# (precompiled, .NET runtime included) and packs it with WiX v5. Run with Windows PowerShell:
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$project = Get-Content (Join-Path $root "src\DualSenseTweaker\DualSenseTweaker.csproj")
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$payload = Join-Path $root "artifacts\payload"

Write-Host "DualSense Tweaker $version – publikujem aplikáciu…"
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
dotnet publish (Join-Path $root "src\DualSenseTweaker") -c $Configuration -r win-x64 --self-contained true -nologo `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none `
    -p:AllowedReferenceRelatedFileExtensions=.none -o $payload
if ($LASTEXITCODE) { throw "Publikovanie zlyhalo." }

if (-not (Test-Path (Join-Path $root "installer\Assets\dialog.bmp"))) {
    & (Join-Path $PSScriptRoot "make-installer-art.ps1")
}

Write-Host "Skladám inštalátor…"
dotnet build (Join-Path $root "installer\DualSenseTweaker.Installer.wixproj") -c $Configuration -nologo `
    -p:ProductVersion=$version -p:PayloadDir=$payload
if ($LASTEXITCODE) { throw "Zostavenie inštalátora zlyhalo." }

$msi = Get-ChildItem (Join-Path $root "installer\bin") -Recurse -Filter "DualSenseTweaker-$version-x64.msi" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$target = Join-Path $root "publish"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $msi.FullName $target -Force
$result = Get-Item (Join-Path $target $msi.Name)
Write-Host ("Hotovo: {0} ({1:N1} MB)" -f $result.FullName, ($result.Length / 1MB))
