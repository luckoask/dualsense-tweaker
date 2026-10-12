# Build the same MSI with separate self-contained feature payloads. Run on Windows.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$project = Get-Content (Join-Path $root "src\DualSenseTweaker\DualSenseTweaker.csproj")
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$payloadRoot = Join-Path $root "artifacts\payload"
$corePayload = Join-Path $payloadRoot "Tweaker"
$macroPayload = Join-Path $payloadRoot "MacroGamer"
$workerPayload = Join-Path $payloadRoot "ScriptWorker"
$dotnetRoot = Split-Path (Get-Command dotnet).Source
$sdkVersion = (dotnet --version).Trim()
$desktopSdk = Join-Path $dotnetRoot "sdk\$sdkVersion\Sdks\Microsoft.NET.Sdk.WindowsDesktop"

function Publish-Application([string]$projectName, [string]$destination) {
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    dotnet publish (Join-Path $root "src\$projectName") -c $Configuration -r win-x64 --self-contained true -nologo `
        -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none `
        -p:AllowedReferenceRelatedFileExtensions=.none -o $destination
    if ($LASTEXITCODE) { throw "Publish failed: $projectName" }
    # Self-contained publish does not automatically preserve every runtime license notice.
    $licenses = Join-Path $destination "licenses"
    New-Item -ItemType Directory -Force $licenses | Out-Null
    foreach ($notice in @(@("LICENSE.txt", "dotnet-LICENSE.txt"), @("ThirdPartyNotices.txt", "dotnet-THIRDPARTYNOTICES.txt"))) {
        $source = Join-Path $dotnetRoot $notice[0]
        if (-not (Test-Path $source)) { throw "Missing SDK/runtime license notice: $source" }
        Copy-Item $source (Join-Path $licenses $notice[1])
    }
    if ($projectName -ne "DualSenseTweaker.MacroGamer.ScriptHost") {
        foreach ($file in @("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT")) {
            $source = Join-Path $desktopSdk $file
            if (-not (Test-Path $source)) { throw "Missing Windows Desktop license notice: $source" }
            Copy-Item $source (Join-Path $licenses "WindowsDesktop-$file")
        }
    }
}

Publish-Application "DualSenseTweaker" $corePayload
Publish-Application "DualSenseTweaker.MacroGamer" $macroPayload
Publish-Application "DualSenseTweaker.MacroGamer.ScriptHost" $workerPayload
# WPF's WindowsBase implementation differs from the console runtime's facade. Keep the
# self-contained worker in its own directory; never overwrite one runtime with another.
$installedWorker = Join-Path $macroPayload "script-worker"
New-Item -ItemType Directory -Force $installedWorker | Out-Null
Copy-Item (Join-Path $workerPayload "*") $installedWorker -Recurse -Force
$unexpected = Get-ChildItem $corePayload -Recurse -File | Where-Object {
    $_.Name -like "*MacroGamer*" -or $_.Name -eq "MoonSharp.Interpreter.dll"
}
if ($unexpected) { throw "Companion dependencies leaked into required payload: $($unexpected.Name -join ', ')" }
foreach ($file in @("MacroGamer.exe", "script-worker\MacroGamer.ScriptHost.exe", "MoonSharp.Interpreter.dll")) {
    if (-not (Test-Path (Join-Path $macroPayload $file))) { throw "Missing optional payload: $file" }
}
if (-not (Test-Path (Join-Path $root "installer\Assets\dialog.bmp"))) {
    & (Join-Path $PSScriptRoot "make-installer-art.ps1")
}
dotnet build (Join-Path $root "installer\DualSenseTweaker.Installer.wixproj") -c $Configuration -nologo `
    -p:ProductVersion=$version -p:PayloadDir=$corePayload -p:MacroPayloadDir=$macroPayload
if ($LASTEXITCODE) { throw "MSI build failed." }
$msi = Get-ChildItem (Join-Path $root "installer\bin") -Recurse -Filter "DualSenseTweaker-$version-x64.msi" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw "MSI was not produced." }
$target = Join-Path $root "publish"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $msi.FullName $target -Force
$result = Get-Item (Join-Path $target $msi.Name)
Write-Host ("Built: {0} ({1:N1} MB)" -f $result.FullName, ($result.Length / 1MB))
