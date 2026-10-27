# ---------------------------------------------------------------------------
#  還原 Armoury Crate 隔離（依備份檔把服務啟動方式與排程還原）
#  需以管理員身分執行
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '請以管理員身分執行'
}

$backupFile = Join-Path $env:LOCALAPPDATA 'BatteryCare\asus-services-backup.json'
if (-not (Test-Path $backupFile)) { throw "找不到備份檔：$backupFile（沒有隔離過？）" }
$backup = Get-Content $backupFile -Raw | ConvertFrom-Json

function Convert-StartMode($mode) {
    switch ($mode) {
        'Auto' { 'auto' }
        'Manual' { 'demand' }
        'Disabled' { 'disabled' }
        default { 'demand' }
    }
}

foreach ($svc in $backup.services) {
    $start = Convert-StartMode $svc.StartMode
    & sc.exe config $svc.Name start= $start | Out-Null
    if ($start -ne 'disabled') { & sc.exe start $svc.Name | Out-Null }
    Write-Host "$($svc.Name): 啟動方式還原為 $start"
}

if ($backup.taskState -eq 'Ready') {
    & schtasks.exe /change /tn "\ASUS\ArmourySocketServer" /enable | Out-Null
    Write-Host '排程 \ASUS\ArmourySocketServer 已重新啟用'
}

if ($null -ne $backup.chargingRate) {
    $hotkeyKey = 'HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys'
    Set-ItemProperty -Path $hotkeyKey -Name ChargingRate -Value ([int]$backup.chargingRate) -Type DWord -ErrorAction SilentlyContinue
    Write-Host "ChargingRate 還原為 $($backup.chargingRate)"
}

Write-Host '完成。下次登入時 Armoury Crate 會照原本設定啟動。'
