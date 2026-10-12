# Destructive ONLY to this product in an isolated Windows test machine. CI runs on a clean hosted VM.
param([Parameter(Mandatory)][string]$Msi, [Parameter(Mandatory)][string]$PreviousMsi)
$ErrorActionPreference = "Stop"
$Msi = (Resolve-Path $Msi).Path
$PreviousMsi = (Resolve-Path $PreviousMsi).Path
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$evidence = Join-Path $root "artifacts\validation\installer"
New-Item -ItemType Directory -Force $evidence | Out-Null
$install = Join-Path $env:ProgramFiles "LCKS\DualSense Tweaker"
$macro = Join-Path $install "MacroGamer"
$data = Join-Path $env:APPDATA "DualSenseTweaker\MacroGamer"
if (Test-Path (Join-Path $install "DualSenseTweaker.exe")) { throw "Use a clean test VM: product is already installed." }

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($Msi, 0)
function Read-Table([string]$query, [int]$columns) {
    $view = $db.OpenView($query); [void]$view.Execute()
    $result = @()
    while ($record = $view.Fetch()) {
        $row = @(); for ($i = 1; $i -le $columns; $i++) { $row += $record.StringData($i) }
        $result += ,$row
    }
    [void]$view.Close(); return ,$result
}
$features = Read-Table 'SELECT `Feature`, `Feature_Parent`, `Level`, `Attributes` FROM `Feature`' 4
$features | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence "features.json")
if (-not ($features | Where-Object { $_[0] -eq "Application" -and $_[2] -eq "1" -and ([int]$_[3] -band 16) })) { throw "Required core feature is missing." }
if (-not ($features | Where-Object { $_[0] -eq "MacroGamer" -and $_[2] -eq "2" -and -not $_[1] })) { throw "Optional feature is not independently off by default." }
if ((Read-Table 'SELECT `Value` FROM `Property` WHERE `Property` = ''INSTALLLEVEL''' 1)[0][0] -ne "1") { throw "Wrong INSTALLLEVEL." }
$files = Read-Table 'SELECT `File`.`FileName`, `Component`.`Directory_`, `FeatureComponents`.`Feature_` FROM `File`, `Component`, `FeatureComponents` WHERE `File`.`Component_` = `Component`.`Component` AND `Component`.`Component` = `FeatureComponents`.`Component_`' 3
$files | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence "files-by-feature.json")
$leaks = $files | Where-Object { $_[2] -eq "Application" -and ($_[0] -match "MacroGamer|MoonSharp" -or $_[1] -eq "MACROFOLDER") }
if ($leaks) { throw "Companion files leaked into core feature." }
if (-not ($files | Where-Object { $_[0] -match 'MacroGamer.exe$' -and $_[2] -eq "MacroGamer" })) { throw "Macro executable is not in its feature." }

$script:step = 0
function Invoke-Msi([string]$operation, [string]$path, [string]$properties = "", [int[]]$expected = @(0,3010)) {
    $script:step++
    $log = Join-Path $evidence ("{0:00}-{1}.log" -f $script:step, $operation.TrimStart('/'))
    $process = Start-Process msiexec.exe -ArgumentList "$operation `"$path`" /qn /norestart /l*v `"$log`" $properties" -PassThru -Wait
    if ($process.ExitCode -notin $expected) { throw "MSI $operation returned $($process.ExitCode). See $log" }
}
function Assert-CoreOnly {
    if (-not (Test-Path (Join-Path $install "DualSenseTweaker.exe"))) { throw "Required app absent." }
    if (Test-Path (Join-Path $macro "MacroGamer.exe")) { throw "Optional executable was installed by default." }
    if (Get-ChildItem $install -Recurse -File | Where-Object { $_.Name -match "MacroGamer|MoonSharp" }) { throw "Optional files present in core-only install." }
    if (Test-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Macro Gamer.lnk")) { throw "Optional shortcut remains." }
    $reg = Get-ItemProperty "HKLM:\Software\LCKS\DualSense Tweaker" -ErrorAction SilentlyContinue
    if ($reg.MacroGamerInstalled) { throw "Optional registry flag remains." }
}
function Assert-Both {
    foreach ($file in @("MacroGamer.exe", "script-worker\MacroGamer.ScriptHost.exe", "MoonSharp.Interpreter.dll")) {
        if (-not (Test-Path (Join-Path $macro $file))) { throw "Missing installed companion file: $file" }
    }
    if (-not (Get-ItemProperty "HKLM:\Software\LCKS\DualSense Tweaker").MacroGamerInstalled) { throw "Optional registry flag absent." }
    if (Get-Process MacroGamer -ErrorAction SilentlyContinue) { throw "Companion started automatically." }
    # Exercise the installed, self-contained worker rather than the test build's worker.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $macro "script-worker\MacroGamer.ScriptHost.exe"
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true
    $worker = [Diagnostics.Process]::Start($start)
    $worker.StandardInput.WriteLine('{"operation":"Validate","source":"on_press(\"R1\", function() key_tap(\"E\", 60) end)"}')
    $worker.StandardInput.Close()
    $response = $worker.StandardOutput.ReadToEnd()
    if (-not $worker.WaitForExit(5000)) { $worker.Kill(); throw "Installed worker did not exit." }
    $response | Set-Content (Join-Path $evidence "installed-worker.json")
    if ($worker.ExitCode -ne 0 -or -not ($response | ConvertFrom-Json).Success) { throw "Installed Lua worker failed: $response" }
    $app = Start-Process (Join-Path $macro "MacroGamer.exe") -PassThru
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        do { Start-Sleep -Milliseconds 100; $app.Refresh() } while (-not $app.HasExited -and -not $app.MainWindowHandle -and $watch.Elapsed.TotalSeconds -lt 10)
        if ($app.HasExited -or -not $app.MainWindowHandle -or -not $app.MainWindowTitle.StartsWith("Macro Gamer ")) {
            Get-ChildItem $data -Filter "startup-error.log" -ErrorAction SilentlyContinue | Copy-Item -Destination $evidence
            throw "Installed companion did not open its main WPF window. Title: $($app.MainWindowTitle); exited: $($app.HasExited)."
        }
        $closer = Start-Process (Join-Path $macro "MacroGamer.exe") -ArgumentList "--exit" -PassThru -Wait
        if ($closer.ExitCode -ne 0 -or -not $app.WaitForExit(15000)) { throw "Installed companion did not close gracefully." }
        "PASS: installed self-contained WPF application opened its main window and exited through --exit." | Set-Content (Join-Path $evidence "installed-ui.txt")
    } finally { if (-not $app.HasExited) { $app.Kill() }; $app.Dispose(); $worker.Dispose() }
}
try {
    Invoke-Msi /i $Msi "ADDLOCAL=MacroGamer" @(1603)
    Invoke-Msi /i $Msi
    Assert-CoreOnly
    Invoke-Msi /i $Msi "ADDLOCAL=Application,Desktop,MacroGamer,MacroDesktop"
    Assert-Both
    New-Item -ItemType Directory -Force $data | Out-Null
    "preserve-user-data" | Set-Content (Join-Path $data "installer-test.marker")
    Remove-Item (Join-Path $macro "MacroGamer.exe")
    Invoke-Msi /fa $Msi
    Assert-Both
    Invoke-Msi /i $Msi "REMOVE=MacroDesktop,MacroGamer"
    Assert-CoreOnly
    if (-not (Test-Path (Join-Path $data "installer-test.marker"))) { throw "Feature removal deleted user data." }
    Invoke-Msi /x $Msi
    if (Test-Path (Join-Path $install "DualSenseTweaker.exe")) { throw "Core executable remains after uninstall." }
    Invoke-Msi /i $PreviousMsi
    Invoke-Msi /i $Msi
    Assert-CoreOnly
    Invoke-Msi /i $Msi "ADDLOCAL=Application,MacroGamer"
    Assert-Both
    Invoke-Msi /x $Msi
    if (Test-Path (Join-Path $macro "MacroGamer.exe")) { throw "Companion remains after uninstall." }
    if (-not (Test-Path (Join-Path $data "installer-test.marker"))) { throw "Uninstall deleted user data." }
    "PASS: optional payload isolation; default install; explicit features; repair; optional removal; 1.0.7 upgrade; uninstall; data preservation." | Set-Content (Join-Path $evidence "result.txt")
} finally {
    # The VM is disposable, but make failures leave no running test application.
    Get-Process MacroGamer,MacroGamer.ScriptHost,DualSenseTweaker -ErrorAction SilentlyContinue | Stop-Process -Force
}
