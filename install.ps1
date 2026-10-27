# ---------------------------------------------------------------------------
#  安裝「電池保養控」到 %LOCALAPPDATA%\BatteryCare（PowerShell 版，免管理員）
#  Windows 11 Smart App Control 會擋未簽章的 exe，所以改用簽章過的 powershell.exe 當宿主。
#  用法：powershell -ExecutionPolicy Bypass -File install.ps1
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$srcStart = Join-Path $root 'start.ps1'
$srcCode = Join-Path $root 'Program.cs'
if (-not (Test-Path $srcCode)) { $srcCode = Join-Path $root 'src\Program.cs' }
if (-not (Test-Path $srcStart)) { throw '找不到 start.ps1' }
if (-not (Test-Path $srcCode)) { throw '找不到 Program.cs' }

$targetDir = Join-Path $env:LOCALAPPDATA 'BatteryCare'
$targetStart = Join-Path $targetDir 'start.ps1'
$targetCode = Join-Path $targetDir 'Program.cs'
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# 1) 停掉正在跑的舊版本（PowerShell 宿主或舊的 exe）
$killed = 0
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @('powershell.exe', 'battery-tray.exe') -and
        $_.CommandLine -and
        ($_.CommandLine -match 'BatteryCare\\start\.ps1' -or $_.CommandLine -match 'battery-tray\.exe')
    } |
    ForEach-Object {
        try {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
            $killed++
        } catch { }
    }
if ($killed -gt 0) { Write-Host "已停止 $killed 個舊的常駐實例" }

# 2) 複製程式
Copy-Item $srcStart $targetStart -Force
Copy-Item $srcCode $targetCode -Force
Remove-Item (Join-Path $targetDir 'battery-tray.exe') -Force -ErrorAction SilentlyContinue
Write-Host "已安裝到 $targetDir"

# 3) 寫入開機常駐 + 套用保養 80%
$install = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
    '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', $targetStart, '--install'
)
if ($install.ExitCode -ne 0) { throw "初始化失敗，exit=$($install.ExitCode)" }

# 4) 桌面捷徑
try {
    $ws = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = $ws.CreateShortcut((Join-Path $desktop '電池保養控.lnk'))
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $lnk.Arguments = '-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $targetStart + '"'
    $lnk.WorkingDirectory = $targetDir
    $lnk.Description = '電池保養控 - 80% 限充 / 臨時充滿'
    $lnk.Save()
    Write-Host '已建立桌面捷徑'
} catch {
    Write-Warning "桌面捷徑建立失敗（不影響功能）：$($_.Exception.Message)"
}

# 5) 啟動常駐
Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', $targetStart
)
Start-Sleep -Seconds 4

$running = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -and $_.CommandLine -match 'BatteryCare\\start\.ps1' }
if ($running) {
    Write-Host "已啟動常駐，pid=$($running[0].ProcessId)"
} else {
    throw '啟動失敗，請看 %LOCALAPPDATA%\BatteryCare\logs'
}
