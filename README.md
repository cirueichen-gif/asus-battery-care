# 電池保養控（ASUS TUF Gaming F16 FX608JMR）

右下角系統匣（`^` 溢出區）的電池充電上限控制器。平常鎖 80%，出門前一鍵充到 100%，拔電或 2 小時後自動回保養。

- 機器：ASUS TUF Gaming F16 FX608JMR（BIOS FX608JMR.312）／Windows 11 Home 26300
- **免管理員權限**（走 `\\.\ATKACPI` IOCTL，而不是需要提權的 `root\wmi`）
- **不需要關掉 Windows 的 Smart App Control**

## 為什麼是 PowerShell 版（不是 exe）

原本編譯成 `battery-tray.exe`，但 Windows 11 的 **Smart App Control** 在 2026-10-22 從「評估模式」轉成「開啟」，開始擋未簽章的執行檔：

```
Code Integrity determined that a process ... attempted to load ...battery-tray.exe
that did not meet the Enterprise signing level requirements
(Policy ID:{0283ac0f-fff1-49ae-ada1-8a933130cad6})
```

所以改成：同一個 C# 程式碼，由**簽章過的 powershell.exe 當宿主**、用 `Add-Type` 在記憶體編譯後執行。功能、行為、選單完全一樣；代價是常駐記憶體約 90 MB（exe 版約 32 MB）。

> exe 版的建置腳本 `build.ps1` 還留著。哪天你關掉 Smart App Control（**注意：關掉後要重裝 Windows 才能再開回來**），就可以改回 exe。

## 安裝位置

| 東西 | 路徑 |
|---|---|
| 程式（宿主） | `%LOCALAPPDATA%\BatteryCare\start.ps1` |
| 程式（C# 原始碼，執行時編譯） | `%LOCALAPPDATA%\BatteryCare\Program.cs` |
| 設定 | `%LOCALAPPDATA%\BatteryCare\config.ini` |
| 日誌 | `%LOCALAPPDATA%\BatteryCare\logs\batterycare-yyyyMMdd.log` |
| 開機啟動 | `HKCU\...\Run` → `BatteryCareTray`（啟動 powershell 跑 start.ps1） |
| 桌面捷徑 | `桌面\電池保養控.lnk` |
| Armoury Crate 備份 | `%LOCALAPPDATA%\BatteryCare\asus-services-backup.json` |

原始碼與工具在 `battery-care\`（工作區裡）。

## 怎麼用

圖示在右下角 `^` 溢出區裡（綠色電池；橘色＋圓點＝臨時充到 100% 中）。點 `^` 找到它，**按右鍵**：

- `充到 100%（這次）` — 解除限充充到飽；**拔掉電源**或**按下後滿 2 小時**自動回保養 80%
- `回到保養 80%` — 立刻限回 80%
- `開機自動常駐`（打勾）
- `把圖示收進 ^` — 若哪天圖示跑到工作列上，按這個收回溢出區
- `開啟資料夾` / `結束`

滑鼠移到圖示上看 tooltip：`電量 96% ｜ 插電中 ｜ 保養 80%`。

## 行為規則

1. **保養是預設，而且會自己回來**。只有按「充到 100%（這次）」才解除限充。
2. 充到 100% 是**限時租約**：拔 AC → 立刻回 80%；按下後滿 2 小時 → 不管插不插電都回 80%；按「回到保養」也立刻回 80%。回 80% 後插回電源**不會**自己變 100%。
3. 睡眠／休眠／重開機後重新對帳：租約過期或已拔電 → 回 80%；還在期限內且插著電 → 維持 100%。
4. 防覆蓋：每 20 秒檢查一次，如果「保養模式卻在充」或「充飽模式卻停充」會重套一次（Armoury Crate／韌體偷改就抓得到）。
5. 每次開機 BIOS 會把上限洗回 100%，程式啟動時會重套目前模式。

## Armoury Crate 隔離（2026-10-22 已執行，可逆）

你討厭的 ASUS 內建系統，已經把它**關掉但沒有刪除**（避免動到不該動的驅動）：

- `ArmouryCrateService`、`ArmouryCrateControlInterface`：**停止 + 設為 Disabled**
- 排程工作 `\ASUS\ArmourySocketServer`：**停用**
- 進程 `ArmouryCrate`、`ArmourySocketServer`、`ArmourySwAgent`、`asus_framework`：**已結束**
- `ChargingRate` 機碼設為 **80**（讓 ASUS 服務和本工具記住同一個值）

### 沒動到、也不能動的

- **`ATKWMIACPIIO` 核心驅動**（`ACPI\ASUS2018`，asussci2.inf 驅動包）→ 這是 `\\.\ATKACPI` 的來源，動了本工具就廢了。已驗證隔離後仍是 `RUNNING`。
- `ASUSOptimization`、`AsusAppService` 等另一組 SCI 服務 → **保留**，因為 Fn 熱鍵／OSD／鍵盤背光要靠它（G-Helper FAQ：沒有它就得有 G-Helper 常駐才有 Fn 功能）。
- Armoury Crate 的 App 本體（Store 套件）→ 沒移除；移除只是多花幾秒，對功能沒差。
- 風扇曲線、效能模式、GPU MUX、BIOS 更新 → 本來就由 BIOS/韌體控制，不受影響。

### 要還原

```powershell
# 以管理員身分執行
powershell -ExecutionPolicy Bypass -File battery-care\tools\asus-restore.ps1
```

會依備份把服務啟動方式、排程、`ChargingRate` 還原；下次登入 Armoury Crate 就照原本設定回來。

### 還想更徹底的話（未執行）

- 用官方 **Armoury Crate Uninstall Tool** 移除整個 AC（G-Helper 官方也建議這樣做）；動完**務必**確認 `sc query ATKWMIACPIIO` 還是 RUNNING。
- 再停 `ASUSOptimization` 可徹底斷掉「開機把上限寫回 100」的來源，但**代價是 Fn 熱鍵／OSD／鍵盤背光失效**（除非改用 G-Helper）。目前沒做，因為本工具開機時會自己重套，不需要付這個代價。
- ⚠️ Windows Update 更新 ASUS System Control Interface 驅動時，INF 可能把 `ASUSOptimization` 等服務的啟動方式重設回 Auto（AC 的不會）。到時重跑一次 `asus-isolate.ps1` 即可。

## 指令列

```powershell
$bc = "$env:LOCALAPPDATA\BatteryCare\start.ps1"
$ps = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"

& $ps -NoProfile -STA -ExecutionPolicy Bypass -File $bc --status    # 目前狀態
& $ps -NoProfile -STA -ExecutionPolicy Bypass -File $bc --set 100   # 充到 100%（常駐中會轉交）
& $ps -NoProfile -STA -ExecutionPolicy Bypass -File $bc --set 80    # 回保養 80%
& $ps -NoProfile -STA -ExecutionPolicy Bypass -File $bc --probe     # 環境探測（ATKACPI 通不通）
& $ps -NoProfile -STA -ExecutionPolicy Bypass -File $bc --install   # 設定開機常駐
& $ps -NoProfile -STA -ExecutionPolicy Bypass -File $bc --uninstall # 取消開機常駐
```

退出碼：`0` 成功、`1` 一般錯誤、`2` 參數錯、`5` 硬體拒絕、`6` 連線失敗、`8` 已由常駐程式控制。

## 重新安裝 / 更新

```powershell
cd battery-care
powershell -ExecutionPolicy Bypass -File install.ps1
```

（會停掉舊實例、複製新檔、重寫開機啟動、建捷徑、重新啟動。）

## 技術說明

- 充電上限：`\\.\ATKACPI` → `DeviceIoControl(0x0022240C)` → `DEVS(0x00120057, 80|100)`，回傳 `1` 代表成功。
  - Linux 核心把 `0x00120057` 叫 `ASUS_WMI_DEVID_RSOC`（Maximum charging percentage）。
  - `DSTS` 讀回值不可信（韌體沒有可靠回讀），狀態以 `config.ini` 為準。
- 拔電偵測：`RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)` 為主、`WM_POWERBROADCAST` 為輔，事件後去抖 1.5 秒；每 20 秒低頻輪詢補顯示與漂移。
- 所有 ACPI 呼叫都在單一背景執行緒序列化；失敗 1/2/4 秒退避重試 3 次，失敗不假裝成功（tooltip 會出現 ⚠）。
- 單一實例（`Local\BatteryCareTray` mutex）；第二次啟動會退出，指令會用視窗訊息轉交給常駐實例。

## 疑難排解

- **找不到圖示**：在 `^` 溢出區裡；點 `^` 展開，或用「在工作列顯示圖示」的舊做法已移除。
- **App 被 Smart App Control 擋**：本版用 powershell 宿主，不會被擋。若你自己另建 exe，會遇到同樣問題。
- **切不過去**：看 `logs` 最新日誌；tooltip 有 ⚠ 就是上次套用失敗。
- **想全部移除**：`--uninstall`、刪 `%LOCALAPPDATA%\BatteryCare`、刪桌面捷徑；Armoury Crate 用 `asus-restore.ps1` 還原。
