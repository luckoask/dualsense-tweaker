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

function Publish-Application([string]$projectName, [string]$destination) {
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    dotnet publish (Join-Path $root "src\$projectName") -c $Configuration -r win-x64 --self-contained true -nologo `
        -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none `
        -p:AllowedReferenceRelatedFileExtensions=.none -o $destination
    if ($LASTEXITCODE) { throw "Publish failed: $projectName" }
}

Publish-Application "DualSenseTweaker" $corePayload
Publish-Application "DualSenseTweaker.MacroGamer" $macroPayload
Publish-Application "DualSenseTweaker.MacroGamer.ScriptHost" $workerPayload
# Worker and WPF app share one private runtime. Reject conflicting files instead of overwriting them.
Get-ChildItem $workerPayload -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($workerPayload.Length).TrimStart([char[]]"\/")
    $destination = Join-Path $macroPayload $relative
    if (Test-Path $destination) {
        if ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash $destination).Hash) {
            throw "Conflicting companion/worker payload: $relative"
        }
    } else {
        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
        Copy-Item $_.FullName $destination
    }
}
$unexpected = Get-ChildItem $corePayload -Recurse -File | Where-Object {
    $_.Name -like "*MacroGamer*" -or $_.Name -eq "MoonSharp.Interpreter.dll"
}
if ($unexpected) { throw "Companion dependencies leaked into required payload: $($unexpected.Name -join ', ')" }
foreach ($file in @("MacroGamer.exe", "MacroGamer.ScriptHost.exe", "MoonSharp.Interpreter.dll")) {
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
