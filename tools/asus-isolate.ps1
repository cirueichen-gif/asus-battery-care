# ---------------------------------------------------------------------------
#  隔離 Armoury Crate（可逆）：停用它的服務與排程，保留 ASUS System Control Interface 驅動。
#  \\.\ATKACPI 由 ATKWMIACPIIO 核心驅動提供，屬 asussci2 驅動包，不受此腳本影響。
#  需以管理員身分執行；先跑 asus-backup 會存在 %LOCALAPPDATA%\BatteryCare\asus-services-backup.json
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '請以管理員身分執行'
}

$backupFile = Join-Path $env:LOCALAPPDATA 'BatteryCare\asus-services-backup.json'
New-Item -ItemType Directory -Force -Path (Split-Path $backupFile) | Out-Null

# 只動 Armoury Crate 這一組；ASUSOptimization / AsusAppService 等 SCI 服務保留（Fn 熱鍵要用）
$armouryServices = @('ArmouryCrateService', 'ArmouryCrateControlInterface')
$armouryTask = 'ASUS\ArmourySocketServer'
$armouryProcesses = @('ArmouryCrate', 'ArmouryCrate.Service', 'ArmouryCrate.UserSessionHelper',
                      'ArmouryCrateControlInterface', 'ArmourySocketServer', 'ArmourySwAgent', 'asus_framework')

# 1) 備份目前狀態（只備份一次，第二次執行不會蓋掉原始備份）
if (-not (Test-Path $backupFile)) {
    $services = @()
    foreach ($n in $armouryServices) {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$n'" -ErrorAction SilentlyContinue
        if ($svc) { $services += [PSCustomObject]@{ Name = $n; StartMode = $svc.StartMode } }
    }
    $task = Get-ScheduledTask -TaskPath '\ASUS\' -TaskName 'ArmourySocketServer' -ErrorAction SilentlyContinue
    $taskState = if ($task) { $task.State.ToString() } else { $null }
    $chargingRate = (Get-ItemProperty 'HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys' -Name ChargingRate -ErrorAction SilentlyContinue).ChargingRate
    @{
        createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        services = $services
        taskState = $taskState
        chargingRate = $chargingRate
    } | ConvertTo-Json -Depth 5 | Set-Content -Path $backupFile -Encoding UTF8
    Write-Host "已備份原始狀態 -> $backupFile"
} else {
    Write-Host "已有備份，不覆蓋：$backupFile"
}

# 2) 停用 Armoury Crate 服務
foreach ($n in $armouryServices) {
    $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Host "跳過（不存在）：$n"; continue }
    & sc.exe stop $n | Out-Null
    Start-Sleep -Milliseconds 700
    & sc.exe config $n start= disabled | Out-Null
    $now = Get-CimInstance Win32_Service -Filter "Name='$n'"
    Write-Host ("{0}: 狀態={1} 啟動方式={2}" -f $n, $now.State, $now.StartMode)
}

# 3) 停用排程
if (Get-ScheduledTask -TaskPath '\ASUS\' -TaskName 'ArmourySocketServer' -ErrorAction SilentlyContinue) {
    & schtasks.exe /change /tn "\ASUS\ArmourySocketServer" /disable | Out-Null
    Write-Host '排程 \ASUS\ArmourySocketServer 已停用'
}

# 4) 結束 Armoury Crate 進程
foreach ($p in $armouryProcesses) {
    Get-Process -Name $p -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); Write-Host "已結束進程：$p (pid $($_.Id))" } catch { Write-Warning "結束 $p 失敗：$($_.Exception.Message)" }
    }
}

# 5) Level 0：讓 ASUS 服務記住的充電上限也等於 80%，避免它開機把 100 寫回來
$hotkeyKey = 'HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys'
Set-ItemProperty -Path $hotkeyKey -Name ChargingRate -Value 80 -Type DWord -ErrorAction SilentlyContinue
Write-Host 'ChargingRate 機碼已設為 80'

# 6) 驗證驅動還在（這是最重要的一行）
& sc.exe query ATKWMIACPIIO | Select-String -Pattern 'STATE'
Write-Host '完成。回報步驟：asus-verify.ps1'
