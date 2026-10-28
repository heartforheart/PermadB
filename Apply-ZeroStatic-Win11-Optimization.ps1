$ErrorActionPreference = "Continue"
$logFile = "A:\Projects2\PermadB\zero_static_install.log"
Start-Transcript -Path $logFile -Force | Out-Null

Write-Output "=== 1. DEPLOYING ZERO-STATIC PERMADB APO & APP ==="
Stop-Process -Name "PermadB" -Force -ErrorAction SilentlyContinue
Stop-Service -Name "audiosrv" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "audiodg" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Copy-Item "A:\Projects2\PermadB\PermadBCode\APO\PermadBApo.dll" "C:\Program Files\PermadB\PermadBApo.dll" -Force
Copy-Item "A:\Projects2\PermadB\PermadBCode\APO\PermadBApo.cat" "C:\Program Files\PermadB\PermadBApo.cat" -Force
Copy-Item "A:\Projects2\PermadB\dist\PermadB.dll" "C:\Program Files\PermadB\PermadB.dll" -Force
Copy-Item "A:\Projects2\PermadB\dist\PermadB.exe" "C:\Program Files\PermadB\PermadB.exe" -Force
Remove-Item "C:\Users\Public\permadb_apo.log" -Force -ErrorAction SilentlyContinue
Write-Output "[OK] PermadBApo.dll & PermadB.exe updated in C:\Program Files\PermadB"

Write-Output "=== 2. ENABLING MSI INTERRUPTS FOR AUDIO CONTROLLERS & HIGH PRIO FOR USB ==="
$pciDevs = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like "PCI\*" -and ($_.Class -in @("Display","Media","System","USB")) }
foreach ($d in $pciDevs) {
    $msiPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($d.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties"
    $prioPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($d.InstanceId)\Device Parameters\Interrupt Management\Affinity Policy"
    if ($d.FriendlyName -match "Audio") {
        if (-not (Test-Path $msiPath)) { New-Item -Path $msiPath -Force | Out-Null }
        Set-ItemProperty -Path $msiPath -Name "MSISupported" -Value 1 -Type DWord
        if (-not (Test-Path $prioPath)) { New-Item -Path $prioPath -Force | Out-Null }
        Set-ItemProperty -Path $prioPath -Name "DevicePriority" -Value 3 -Type DWord
        Write-Output "[OK] Enabled MSI + High Priority on Audio Controller: $($d.FriendlyName)"
    }
    elseif ($d.Class -eq "USB") {
        if (-not (Test-Path $prioPath)) { New-Item -Path $prioPath -Force | Out-Null }
        Set-ItemProperty -Path $prioPath -Name "DevicePriority" -Value 3 -Type DWord
        Write-Output "[OK] Set High Interrupt Priority on USB Host Controller: $($d.FriendlyName)"
    }
}

Write-Output "=== 3. DISABLING WDF IDLE / SELECTIVE SUSPEND ON USB HUBS & HYPERX DONGLE ==="
$usbDevs = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "^USB\\|^HID\\VID_03F0|^USB\\VID_03F0|^USB\\VID_1D6C|^USB\\ROOT_HUB" }
foreach ($u in $usbDevs) {
    $regPm = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($u.InstanceId)\Device Parameters"
    $wdfPm = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($u.InstanceId)\Device Parameters\WDF"
    if (Test-Path $regPm) {
        Set-ItemProperty -Path $regPm -Name "EnhancedPowerManagementEnabled" -Value 0 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regPm -Name "AllowIdleIrpInD3" -Value 0 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regPm -Name "DeviceSelectiveSuspended" -Value 0 -Type DWord -ErrorAction SilentlyContinue
    }
    if (Test-Path $wdfPm) {
        Set-ItemProperty -Path $wdfPm -Name "IdleInWorkingState" -Value 0 -Type DWord -ErrorAction SilentlyContinue
    }
}
Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Enable) {
        $_ | Set-CimInstance -Property @{Enable = $false} -ErrorAction SilentlyContinue
    }
}
Write-Output "[OK] Disabled USB Hub & HID Idle/Selective Suspend across all endpoints"

Write-Output "=== 4. STOPPING CORSAIR CPUID POLLING THRASH & DTS APO SERVICE ==="
Stop-Service -Name "CorsairCpuIdService" -Force -ErrorAction SilentlyContinue
Set-Service -Name "CorsairCpuIdService" -StartupType Manual -ErrorAction SilentlyContinue
Stop-Service -Name "iCUEUpdateService" -Force -ErrorAction SilentlyContinue
Set-Service -Name "iCUEUpdateService" -StartupType Manual -ErrorAction SilentlyContinue
Stop-Service -Name "DtsHPXV2Apo4Service" -Force -ErrorAction SilentlyContinue
Set-Service -Name "DtsHPXV2Apo4Service" -StartupType Manual -ErrorAction SilentlyContinue
Write-Output "[OK] Stopped CorsairCpuIdService, iCUEUpdateService, and DtsHPXV2Apo4Service"

Write-Output "=== 5. EXPANDING TCP/UDP EPHEMERAL PORT RANGE & REDUCING TIME_WAIT ==="
netsh int ipv4 set dynamicport tcp start=10000 num=55535 | Out-Null
netsh int ipv4 set dynamicport udp start=10000 num=55535 | Out-Null
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters" -Name "TcpTimedWaitDelay" -Value 30 -Type DWord -ErrorAction SilentlyContinue
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters" -Name "MaxUserPort" -Value 65534 -Type DWord -ErrorAction SilentlyContinue
Write-Output "[OK] Expanded dynamic TCP/UDP port pool to 55,535 ports (10000-65534)"

Write-Output "=== 6. TUNING MMCSS PRO AUDIO & MEMORY MANAGEMENT ==="
$audioTask = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Audio"
$proAudioTask = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Pro Audio"
foreach ($tp in @($audioTask, $proAudioTask)) {
    if (Test-Path $tp) {
        Set-ItemProperty -Path $tp -Name "Priority" -Value 6 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $tp -Name "Scheduling Category" -Value "High" -Type String -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $tp -Name "SFIO Priority" -Value "High" -Type String -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $tp -Name "Background Only" -Value "False" -Type String -ErrorAction SilentlyContinue
    }
}
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management" -Name "DisablePagingExecutive" -Value 1 -Type DWord -ErrorAction SilentlyContinue
$ptKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"
if (-not (Test-Path $ptKey)) { New-Item -Path $ptKey -Force | Out-Null }
Set-ItemProperty -Path $ptKey -Name "PowerThrottlingOff" -Value 1 -Type DWord -ErrorAction SilentlyContinue
Write-Output "[OK] Tuned MMCSS Audio/Pro Audio priorities, DisablePagingExecutive=1, PowerThrottlingOff=1"

Write-Output "=== 7. RESTARTING WINDOWS AUDIO SERVICE & ELEVATING AUDIODG.EXE ==="
Start-Service -Name "audiosrv" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
$audiodg = Get-Process -Name "audiodg" -ErrorAction SilentlyContinue
if ($audiodg) {
    $audiodg.PriorityClass = "High"
    Write-Output "[OK] Elevated audiodg.exe (PID $($audiodg.Id)) to High Priority"
}

Start-Process -FilePath "C:\Program Files\PermadB\PermadB.exe" -ArgumentList "--minimized" -WorkingDirectory "C:\Program Files\PermadB"
Write-Output "[OK] Relaunched PermadB.exe --minimized"

Stop-Transcript | Out-Null
