# CH397Bridge installer. Run install.cmd (it asks for administrator rights and calls this script).
#
# 1. Compiles ch397bridge.exe and force-winusb.exe from src\ with the .NET Framework compiler built into Windows.
# 2. Installs the TAP-Windows6 virtual adapter driver from the official, signed OpenVPN installer
#    and creates a dedicated adapter called "Ethernet (CH397)".
# 3. Waits for the USB adapter, switches it out of its "driver CD" mode and binds Microsoft's WinUSB driver to it.
# 4. Registers a startup task that runs the bridge in the background, and starts it.

param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$InstallDir  = Join-Path $env:ProgramFiles 'CH397Bridge'
$DataDir     = Join-Path $env:ProgramData 'CH397Bridge'
$AdapterName = 'Ethernet (CH397)'
$TaskName    = 'CH397 Ethernet Bridge'
$OpenVpnVer  = '2.7.7-I001'
$DevicePattern = '^USB\\VID_1A86&PID_539[4-7]\\'

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Info([string]$text) { Write-Host "    $text" }

function Get-TapAdapters {
    Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object InterfaceDescription -like 'TAP-Windows Adapter V9*'
}

function Get-MsiProperty([string]$msi, [string]$name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $view = $installer.OpenDatabase($msi, 0).OpenView("SELECT Value FROM Property WHERE Property = '$name'")
    $view.Execute()
    $view.Fetch().StringData(1)
}

function Invoke-Eject([string]$drive) {
    (New-Object -ComObject Shell.Application).Namespace(17).ParseName("$drive\").InvokeVerb('Eject')
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required. Run install.cmd instead of this script.'
}

# Architecture: 12 = ARM64, 9 = x64
$cpuArch = (Get-CimInstance Win32_Processor | Select-Object -First 1).Architecture
$msiArch = switch ($cpuArch) { 12 { 'arm64' } 9 { 'amd64' } default { throw "Unsupported CPU architecture ($cpuArch)." } }
if ($msiArch -ne 'arm64' -and -not $Force) {
    Write-Host 'This PC is not ARM64. WCH''s official driver (on the adapter''s built-in CD) works on x86/x64 and is' -ForegroundColor Yellow
    Write-Host 'the better choice. Re-run with "install.cmd -Force" to install CH397Bridge anyway.' -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
$work = Join-Path $env:TEMP ('CH397Bridge-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Step 'Stopping any running copy'
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $TaskName }
    Get-Process ch397bridge -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    Step 'Compiling CH397Bridge'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path $csc)) { throw '.NET Framework 4 compiler (csc.exe) not found.' }
    & $csc /nologo /optimize /platform:anycpu /target:winexe "/out:$InstallDir\ch397bridge.exe" "$PSScriptRoot\src\Program.cs"
    if ($LASTEXITCODE) { throw 'Compiling ch397bridge.exe failed.' }
    & $csc /nologo /optimize /platform:anycpu "/out:$InstallDir\force-winusb.exe" "$PSScriptRoot\src\ForceWinUsb.cs"
    if ($LASTEXITCODE) { throw 'Compiling force-winusb.exe failed.' }
    Copy-Item "$PSScriptRoot\uninstall.ps1", "$PSScriptRoot\uninstall.cmd" $InstallDir -Force
    Info "Installed to $InstallDir"

    Step "Getting the TAP-Windows6 driver (OpenVPN $OpenVpnVer, $msiArch)"
    $msi = Join-Path $work "OpenVPN-$OpenVpnVer-$msiArch.msi"
    $url = "https://swupdate.openvpn.net/community/releases/OpenVPN-$OpenVpnVer-$msiArch.msi"
    curl.exe -fsSL --retry 3 -o $msi $url
    if ($LASTEXITCODE) { throw "Download failed: $url" }
    $sig = Get-AuthenticodeSignature $msi
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike 'CN=OpenVPN Inc.*') {
        throw "The OpenVPN installer signature is not valid ($($sig.Status)). Aborting."
    }
    Info 'Signature verified: OpenVPN Inc.'

    # Extract (not install) the MSI to get tapctl.exe, OpenVPN's tool for creating TAP adapters.
    $extract = Join-Path $work 'extract'
    Start-Process msiexec.exe -Wait -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$extract`"" | Out-Null
    $tapctl = Get-ChildItem $extract -Recurse -Filter tapctl.exe | Select-Object -First 1
    if (-not $tapctl) { throw 'tapctl.exe not found in the OpenVPN installer.' }
    Copy-Item $tapctl.FullName $InstallDir -Force
    $tapctlExe = Join-Path $InstallDir 'tapctl.exe'

    $tapDriverInstalled = (pnputil /enum-drivers | Out-String) -match 'TAP-Windows Provider V9'
    if (-not $tapDriverInstalled) {
        $before = @(Get-TapAdapters | ForEach-Object InterfaceGuid)
        Info 'Installing the TAP-Windows6 driver (driver only, no VPN software)...'
        $p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList "/i `"$msi`" ADDLOCAL=Drivers,Drivers.TAPWindows6 /qn /norestart"
        if ($p.ExitCode -notin 0, 3010) { throw "TAP-Windows6 driver install failed (msiexec exit code $($p.ExitCode))." }
        @{ ProductCode = (Get-MsiProperty $msi 'ProductCode') } | ConvertTo-Json | Set-Content (Join-Path $DataDir 'openvpn-installed.json')
        # The OpenVPN installer creates one adapter itself; make that one ours.
        $new = Get-TapAdapters | Where-Object { $before -notcontains $_.InterfaceGuid } | Select-Object -First 1
        if ($new -and -not (Get-TapAdapters | Where-Object Name -eq $AdapterName)) {
            Rename-NetAdapter -Name $new.Name -NewName $AdapterName
        }
    }
    else {
        Info 'TAP-Windows6 driver already installed.'
    }

    Step "Creating the network adapter '$AdapterName'"
    if (Get-TapAdapters | Where-Object Name -eq $AdapterName) {
        Info 'Already exists.'
    }
    else {
        & $tapctlExe create --hwid 'root\tap0901' --name $AdapterName
        if ($LASTEXITCODE) { throw 'Creating the TAP adapter failed.' }
        Info 'Created.'
    }

    Step 'Setting up the USB adapter'
    $winusbDevices = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match $DevicePattern -and $_.Service -eq 'WinUSB' })
    if ($winusbDevices) {
        Info "Already using WinUSB: $($winusbDevices[0].InstanceId)"
    }
    else {
        Write-Host '    Plug in the USB Ethernet adapter now (waiting up to 2 minutes)...' -ForegroundColor Yellow
        pnputil /scan-devices | Out-Null # re-detect an adapter whose driver was just removed
        $dev = $null
        $deadline = (Get-Date).AddMinutes(2)
        while (-not $dev -and (Get-Date) -lt $deadline) {
            $dev = Get-PnpDevice -PresentOnly | Where-Object InstanceId -match $DevicePattern | Select-Object -First 1
            if ($dev) { break }
            # The adapter first appears as a CD with WCH's Windows driver; ejecting it switches it to network mode.
            Get-CimInstance Win32_CDROMDrive | Where-Object { $_.Drive -and (Test-Path "$($_.Drive)\WCHUSBNIC\WCHUSBNIC.INF") } |
                ForEach-Object { Info "Ejecting the adapter's driver CD ($($_.Drive))..."; Invoke-Eject $_.Drive }
            Start-Sleep -Milliseconds 500
        }
        if (-not $dev) { throw 'The adapter was not detected. Unplug it, plug it back in and run install.cmd again.' }
        Info "Found $($dev.FriendlyName) ($($dev.InstanceId))"
        & (Join-Path $InstallDir 'force-winusb.exe') $dev.InstanceId
        if ($LASTEXITCODE) { throw 'Installing the WinUSB driver for the adapter failed.' }
    }

    Step 'Registering the background service'
    $action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'ch397bridge.exe') -Argument "--adapter `"$AdapterName`"" -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $taskUser = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Description 'CH397 USB Ethernet bridge (CH397Bridge). Remove with uninstall.cmd.' `
        -Action $action -Trigger $trigger -Settings $settings -Principal $taskUser -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Info "Task '$TaskName' starts the bridge at every boot."

    Step 'Checking the connection'
    $log = Join-Path $DataDir 'bridge.log'
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Bridge running' -Quiet))) {
        Start-Sleep -Seconds 1
    }
    if (Test-Path $log) { Get-Content $log -Tail 5 | ForEach-Object { Info $_ } }
    $nic = Get-NetAdapter -Name $AdapterName -ErrorAction SilentlyContinue
    if ($nic) { Info "Adapter '$AdapterName' is $($nic.Status)." }

    Write-Host ''
    Write-Host 'Done. Plug an Ethernet cable into the adapter and it will connect automatically.' -ForegroundColor Green
    Write-Host "Log file: $log"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
