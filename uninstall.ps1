# Removes everything install.ps1 set up. Run uninstall.cmd (it asks for administrator rights).

$ErrorActionPreference = 'Continue'

$InstallDir  = Join-Path $env:ProgramFiles 'CH397Bridge'
$DataDir     = Join-Path $env:ProgramData 'CH397Bridge'
$AdapterName = 'Ethernet (CH397)'
$TaskName    = 'CH397 Ethernet Bridge'
$DevicePattern = '^USB\\VID_1A86&PID_539[4-7]\\'

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Administrator rights are required. Run uninstall.cmd instead of this script.' -ForegroundColor Red
    exit 1
}

Step 'Stopping the bridge'
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Get-Process ch397bridge -ErrorAction SilentlyContinue | Stop-Process -Force

Step "Removing the '$AdapterName' network adapter"
$tapctl = Join-Path $InstallDir 'tapctl.exe'
$tap = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object Name -eq $AdapterName
if ($tap -and (Test-Path $tapctl)) { & $tapctl delete $tap.InterfaceGuid }

$marker = Join-Path $DataDir 'openvpn-installed.json'
if (Test-Path $marker) {
    # Only remove the TAP driver if CH397Bridge installed it (an existing OpenVPN setup is left alone).
    $productCode = (Get-Content $marker -Raw | ConvertFrom-Json).ProductCode
    Write-Host "    Uninstalling the TAP-Windows6 driver ($productCode)..."
    Start-Process msiexec.exe -Wait -ArgumentList "/x $productCode /qn /norestart" | Out-Null
}

Step 'Releasing the USB adapter from WinUSB'
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match $DevicePattern -and $_.Service -eq 'WinUSB' } |
    ForEach-Object {
        Write-Host "    $($_.InstanceId)"
        pnputil /remove-device $_.InstanceId | Out-Null
    }

Step 'Removing files'
Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
# This script may be running from the install folder, so delete it after we exit.
Start-Process cmd.exe -WindowStyle Hidden -ArgumentList "/c timeout /t 3 >nul & rmdir /s /q `"$InstallDir`""

Write-Host ''
Write-Host 'CH397Bridge has been removed. Unplug and replug the adapter.' -ForegroundColor Green
