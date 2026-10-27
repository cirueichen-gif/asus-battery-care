# 在 TUF Gaming F16 FX608JMR 上停用/移除 Armoury Crate，同時保住 `\\.\ATKACPI`

> 本機實測 + 官方文件 + 社群交叉查證。日期 2026-10-22。

## 0. 一句話結論

`\\.\ATKACPI` 不是 Armoury Crate 給的，是 **驅動包 asussci2.inf 裡的核心驅動服務 `ATKWMIACPIIO`** 給的。Armoury Crate（App + 它的 4 個服務）跟它是**兩套東西**，可以整包拆掉；但驅動包本身（含 `ASUSOptimization`）不能拆。真正會覆寫你充電上限的是 `ASUSOptimization`，不是 Armoury Crate。

---

## 1. 架構真相（本機證據）

| 事實 | 證據 |
|---|---|
| `\\.\ATKACPI` 由核心驅動提供 | `sc query ATKWMIACPIIO` → `TYPE: 1 KERNEL_DRIVER`、`STATE: 4 RUNNING`、`Start=1 (SYSTEM_START)` |
| 該驅動綁在 ASCI 裝置上 | 裝置 `ACPI\ASUS2018`「ASUS System Control Interface v3」，`oem169.inf`，版本 `3.1.72.0`，狀態 OK |
| `oem169.inf` 就是 `asussci2.inf` 的發佈副本 | INF 內 `ServiceBinary = %13%\%OptimizationFolder%\AsusWmiAcpi.sys`，路徑正是 `asussci2.inf_amd64_197d319fab43e0fe` |
| **同一個 INF 裝了 7 個服務**（關鍵） | AddService：`ATKWMIACPIIO`、`ASUSOptimization`、`AsusAppService`、`ASUSSoftwareManager`、`ASUSSwitch`、`ASUSSystemAnalysis`、`ASUSSystemDiagnosis` |
| Armoury Crate 是 MSIX App | `B9ECED6F.ArmouryCrate 6.7.6.0`；另有 `ArmouryDevice`（`asus_framework.exe`） |
| 三個排程工作屬於 ArmouryDevice | `\ASUS\AcPowerNotification`、`\ASUS\ArmourySocketServer`、`\ASUS\P508PowerAgent` |
| `AsusOptimizationStartupTask.exe` 是服務的子程序 | 父 PID = `AsusOptimization.exe`（PID 4864）→ 停服務就一起消失，不需另外處理 |

**所以「一個 INF、兩層」**：核心層 `ATKWMIACPIIO`（boot-start）＋ 使用者層 6 個服務。停使用者層不會卸載核心層。

---

## 2. 服務分家表（風險分級）

| 服務 | 來源 | 風險 | 停用後 |
|---|---|---|---|
| **`ATKWMIACPIIO`** | asussci2 核心驅動 | ⛔ **絕對不能動** | `\\.\ATKACPI` 直接消失，你的工具全廢 |
| `ASUSOptimization` | asussci2 | 🟡 中 | Fn 熱鍵、OSD、鍵盤背光自動關閉、電池保養設定全失效；**ATKACPI 不受影響** |
| `AsusAppService` | asussci2 | 🟢 低 | MyASUS 與底層溝通（G-Helper 官方列為可停） |
| `ASUSSoftwareManager` | asussci2 | 🟢 低 | 更新派送 |
| `ASUSSwitch` | asussci2 | 🟡 中 | 部分機型 GPU/顯示切換輔助元件（先用 G-Helper 驗證再停） |
| `ASUSSystemAnalysis` / `ASUSSystemDiagnosis` | asussci2 | 🟢 低 | 遙測、診斷（常被點名 CPU 尖峰來源） |
| `ArmouryCrateService` | AC | 🟢 低 | AC 主服務（本來就要移除） |
| `ArmouryCrateControlInterface` | AC（`C:\Windows\System32\ASUSACCI\`） | 🟢 低 | AC 硬體控制代理 |
| `ArmourySocketServer` | ArmouryDevice（排程＋process） | 🟢 低 | AC 內部 IPC |
| `ArmourySwAgent` / `asus_framework` | ArmouryDevice | 🟢 低 | AC 裝置抽象層、韌體通道 |
| `AsusCertService` | 獨立安裝器 | 🟢 低 | 憑證/授權（G-Helper 列可停） |
| `AsusMsControl` | 獨立 | 🟡 未知 | 細節我不確定，先別碰 |
| `AsusScreenXpertHostService` | ScreenXpert | 🟢 低 | 螢幕分割/ScreenPad，與硬體無關 |
| `AsusIMESystemService` | 華碩輸入法 | 🟢 低 | 華碩輸入法（你用微軟注音就沒差） |
| `AsusPTPService` / `AsusPTPDrv` | asusptpfilter.inf | 🟡 保留 | 觸控板手勢會怪 |
| `Asusgio3`（`AsIO3.sys`） | 獨立 | ⛔ 保留 | GPIO/IO，很多工具（風扇、燈效）靠它 |
| `AsusSAIO` | asussci2 | 🟢 留著無妨 | System Analysis 的 IO 驅動（Manual） |
| `asus` / `asusm` | ASUS Update Service | 🟡 | 驅動/BIOS 更新派送，**也是 AC 自動重裝彈窗的來源之一** |

---

## 3. Q1：Armoury Crate 與 ASCI 可以分開移除嗎？

**可以，而且是官方設計就分開的。**

- G-Helper 官方 Requirements 頁把兩者分開列：ASCI 是 **mandatory** 需求（要自己裝 `ASUSSystemControlInterfaceV3.exe`），Armoury Crate 是「建議移除」。
- ASUS 官方 FAQ 1037674 Step 5 也說：裝完 AC 後「還要另外裝 MyASUS UWP 與 **ASUS System Control Interface V2/3**」。

**G-Helper 官方原文（Requirements 頁，逐字）**：

> **Requirements (mandatory)**
> - [.NET 10 Desktop Runtime](...)
> - [Asus System Control Interface](...) (most probably you have it already)
>
> **Recommendations (optional)**
> - It's **not recommended** to use the app in combination with Armoury Crate services, because they adjust the same settings. You can uninstall it using [Armoury Crate Uninstall Tool](https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/Armoury_Crate_Uninstall_Tool.zip). Just in case, you can always install it back later.
> - It's **not recommended** to have "ASUS Smart Display Control" app running, as it will try to change refresh rates and fight with g-helper for the same function. You can safely uninstall it.
> - If you don't plan to use MyASUS you can stop / disable unnecessary services: Go to **Extra** in the app, and press `Stop` in Asus Services section.
>
> — <https://github.com/seerge/g-helper/wiki/Requirements>

**G-Helper 對 Linux 的說明也直接點破驅動歸屬**：

> It relies on the **Asus System Control Interface (ACPI WMI) driver** that ships with Windows, which has no direct equivalent on Linux.

**移除 Armoury Crate 後 `\\.\ATKACPI` 還在不在？**
在。邏輯上：`\\.\ATKACPI` 的裝置節點由 boot-start 的核心驅動 `ATKWMIACPIIO` 建立，AC 的四個服務不持有它。實務上：G-Helper issue #2652 的 TUF 使用者「uninstalling Armoury Crate and MyASUS」後，G-Helper 照樣設得動充電上限（詳見 §7）。

⚠️ 唯一會弄死它的是「**解除安裝 ASUS System Control Interface 驅動**」或「在裝置管理員移除 `ACPI\ASUS2018` 並勾刪驅動」。別做。

---

## 4. Q2：三種官方移除方式，各刪什麼

| 方式 | 刪什麼 | 會刪 SCI 嗎 | 評價 |
|---|---|---|---|
| **Armoury Crate Uninstall Tool**（官方） | 官方說法：「uninstall **all Armoury Crate related apps and services**」。實務上含 AC App、ArmouryCrateService、ArmouryCrateControlInterface、Aura Service、ROG Live Service、ASUS Framework Service 等 | **官方文件沒承諾保留，社群回報也不一**。預期不會（因為它不是 AC 的元件），但**這是你要實測確認的第一件事** | ✅ 首選（G-Helper 作者指定） |
| AC 內建解除安裝 / 「應用程式與功能」 | 只會拿掉 MSIX App（`B9ECED6F.ArmouryCrate`），服務、`ArmouryDevice`、排程全部留著 | 不會 | ❌ 不乾淨，殘留會繼續跟你搶設定 |
| 手動逐項（官方 FAQ 1-6 清單） | `ARMOURY CRATE`、`ARMOURY CRATE Service`、`ASUS Framework Service`、`Aura Service`、`ROG Live Service` | 不會 | ✅ 最可控，適合你想精準只拆 AC 的情況 |

官方下載點：
- Armoury Crate Uninstall Tool：<https://www.asus.com/supportonly/Armoury%20Crate/HelpDesk_Download/>（Driver & Utility → Windows 10/11 64-bit）
- 直接 zip（G-Helper 文件用的）：<https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/Armoury_Crate_Uninstall_Tool.zip>
- 官方流程說明：<https://rog.asus.com/us/support/faq/1041654/>（§1-2）／<https://rog.asus.com/us/support/faq/1037674/>（Uninstallation）

**怎麼救回來**：
- AC：官網抓 `ArmouryCrateInstallTool.zip` → `ArmouryCrateInstaller.exe`；或 MS Store 搜 Armoury Crate。
- ASCI：`ASUSSystemControlInterfaceV3.exe`（G-Helper Requirements 頁那條連結，指向 `dlcdnets.asus.com/.../ASUSSystemControlInterfaceV3/ASUSSystemControlInterfaceV3.exe`）。裝完重開機，`ATKWMIACPIIO` 就會回來。

**驗證指令（移除後必跑）**：
```powershell
sc.exe query ATKWMIACPIIO          # 要 STATE: 4 RUNNING
Get-PnpDevice -InstanceId 'ACPI\ASUS2018\2&DABA3FF&0' | Select Status,Problem   # 要 OK / CM_PROB_NONE
```

---

## 5. Q3：只停用不移除的最小安全做法

### 5.1 先講你的真正問題

`HKLM\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys\ChargingRate` 目前是 **0x64 = 100**。這個值由 `ASUSOptimization` 寫入，開機時它會把 EC 的上限重設成 100 —— 這就是你設 80 之後被蓋掉的原因。G-Helper FAQ 自己承認這件事：

> **Battery charge limiter is not working** — It could be that Asus services are overwriting this limit after. You can stop them from `Extra` settings by clicking `Stop` button.

### 5.2 三個方案

**Level 0（最保守，不動任何服務）**
把 ASUS 那邊也設成 80：MyASUS 裡設 80%，或直接把機碼改成 `0x50`（需管理員，改完重開機）。
```powershell
Set-ItemProperty 'HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys' -Name ChargingRate -Value 0x50 -Type DWord
```
風險：**低**。但「`ASUSOptimization` 開機是否讀這個值去套 EC」**需實測**（我的推論，未經證實）。

**Level 1（推薦：拆 AC，保留全部 asussci2 服務）**
只動 Armoury Crate 那一坨，Fn 熱鍵與觸控板完全正常，ATKACPI 零風險。缺點：`ASUSOptimization` 還在，開機仍可能把上限拉回 100。
```powershell
# 停用 AC 的服務
'sc.exe','config','ArmouryCrateService','start=','disabled'
sc.exe config ArmouryCrateService start= disabled
sc.exe stop  ArmouryCrateService
sc.exe config ArmouryCrateControlInterface start= disabled
sc.exe stop  ArmouryCrateControlInterface
# 停用 ArmouryDevice 的排程
schtasks /change /tn "\ASUS\ArmourySocketServer" /disable
schtasks /change /tn "\ASUS\AcPowerNotification" /disable
# 停用 AC MSIX
Get-AppxPackage B9ECED6F.ArmouryCrate | Remove-AppxPackage
```
（`ArmourySwAgent`、`asus_framework` 是 `ArmouryDevice` 帶起來的 process，沒有獨立服務；AC 移除後就不會啟動。）

**Level 2（解決覆寫問題的根本：再加停 `ASUSOptimization`）**
```powershell
sc.exe config ASUSOptimization start= disabled
sc.exe stop  ASUSOptimization
```
代價（G-Helper FAQ 明講）：

> **My hotkeys don't work** — You must have either G-Heper running in system tray (so don't `Quit` it) or **Asus Optimization service** (if you prefer that) for most of the Fn-keys to work.

也就是：**停 `ASUSOptimization` = Fn 熱鍵、OSD、鍵盤背光自動關閉、麥克風靜音鍵全部失效**，除非有 G-Helper 常駐接手。風險：**中**。ATKACPI 不受影響（不同服務、不同層）。

**Level 3（不要做）**
解除安裝 ASCI 驅動、`pnputil /delete-driver asussci2.inf`、或跑那種「徹底清除奧創全家桶」腳本 —— 會把 Fn 熱鍵、觸控板手勢一起帶走，而且 `\\.\ATKACPI` 消失。有社群腳本自己在 README 就警告：

> On ASUS laptops, this will also remove: Fn hotkey functionality (brightness, volume, etc.), Touchpad gesture support.

### 5.3 關鍵驗證實驗（做完 Level 2 必跑）

```powershell
sc.exe stop ASUSOptimization
# 然後用你的工具設 80，確認 DEVS 回傳 1
# 再重開機，看上限是否維持
sc.exe start ASUSOptimization    # 想還原就這行
```
若停掉後你的工具照樣成功、且開機不再被拉回 100 → 這才是你要的答案。

### 5.4 停用後開機會不會自己回來

- `Start=4 (Disabled)` 跨重開機有效，**不會**自己變回自動。
- 但 **Windows Update 若推送 ASCI 驅動更新，INF 會重跑安裝，把 `ASUSOptimization` 等服務的 StartType 重設回 INF 預設值（Auto）**。你的 `3.1.72.0` 的 `DriverVer = 09/14/2026` 就是剛更新過的痕跡 → 這件事**很可能發生**。AC 的服務不會因此回來。
- `AsusOptimizationStartupTask` 沒有獨立排程、沒有 Run key，父進程是 `AsusOptimization.exe` → 服務停它就停。
- AC 的自動重裝彈窗來源之一是 `asus`/`asusm`（ASUS Update Service），G-Helper FAQ 的解法是「把所有 ASUS 服務停掉」。

---

## 6. Q4：副作用清單

| 項目 | 影響 | 說明 |
|---|---|---|
| 風扇曲線 | ❌ 不受影響 | 風扇由 BIOS/EC 即時控制，AC 也從來不是即時控制（G-Helper FAQ 原文：「It doesn't and can't control your fans. Firmware / BIOS controls them in real-time.」） |
| 效能模式（Silent/Balanced/Turbo） | ❌ 不受影響 | 模式「baked into BIOS」，用同一組 ATKACPI 端點就能切 |
| GPU MUX（Ultimate/Eco） | ⚠️ 看機型 | 走 ATKACPI 端點，AC 不是必要；但 `ASUSSwitch` 停用前建議先用 G-Helper 驗證切換正常 |
| Aura 燈效 | ⚠️ 要替代品 | AC 移除後 Aura GUI 沒了；鍵盤背光可由 G-Helper / 自寫 ATKACPI 控制。Windows 11 的 Dynamic Lighting 也可能插手（G-Helper FAQ 有提到要關掉） |
| MyASUS | ⚠️ 會搶 | G-Helper FAQ：「MyASUS may override the battery charge limit that you set before. My advice... set the same limit in both.」→ 想乾淨就別留 MyASUS |
| Windows Update 裝回來 | ⚠️ 會 | 驅動包會被 WU 更新（見 §5.4）；AC App 本體來自 MS Store，可在 Store 關閉自動更新（ASUS FAQ 1041654 §1-4 有教，但那是全域設定） |
| BIOS 更新需不需要 AC | ❌ 不需要 | BIOS 內建 EZ Flash（開機 F2 → Advanced → ASUS EZ Flash）就能刷；MyASUS 或官網抓檔也可以。G-Helper 本身也有 BIOS 下載功能 |
| 隨機小毛病 | ⚠️ | 社群回報 AC Uninstaller 在部分 Strix 機型會搞混巨集鍵，解法是 EC reset（關機、長按電源 30–40 秒）。你的 TUF 沒巨集鍵，理論上無關 |

---

## 7. Q6：社群證據（明確講「移除 AC 後照樣能控制充電上限」）

**G-Helper issue #2652** — <https://github.com/seerge/g-helper/issues/2652>
發文者是 TUF A15 使用者，欄位直接填「Armoury Crate: Uninstalled」，並說「I have 8 services running even after uninstalling Armoury Crate and MyASUS」。作者 seerge 回覆（逐字）：

> Both Asus services and G-Helper do same thing - they **set charge limit value in BIOS with a special command**.
>
> G-Helper **does not rely on that service** and does not adjust that file :)

以及後續：

> All Asus devices reset all their settings when they reboot, including charging limit.

發文者最後確認：「charge limit is working indeed. But if the laptop is turned off and I plug in when off, it starts charging over the limit until I turn on the laptop and G-Helper autostarts」。

→ **這正是你的情境**：移除 AC 後直接寫 ATKACPI 完全可行；限制是「每次重開機/EC reset 後要重套」，所以你的工具得在登入時自動跑（你已經有 `BatteryCareTray` 在 HKCU Run，這點沒問題）。

其他：
- G-Helper FAQ「What services can be stopped」清單本身就含 `ASUSOptimization`、`ArmouryCrateControlInterface`、`AsusAppService`、`AsusCertService` 等 → 作者背書這些停了不會壞事（代價是對應功能）。
- 反例警告：`crystal-clear-asus` 這類腳本會連驅動一起刪，README 自己承認會失去 Fn 熱鍵與觸控板 —— 別用。

---

## 8. Q5：可逆性（動手前先做）

```powershell
# 1) 備份所有 ASUS 服務的 StartType
$names = (Get-Service | Where-Object { $_.Name -match 'Armoury|Asus|ASUS|ATKWMI' }).Name
$names | ForEach-Object {
  $s = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$_" -ErrorAction SilentlyContinue
  [PSCustomObject]@{ Name=$_; Start=$s.Start; Delayed=$s.DelayedAutoStart }
} | Export-Csv "$env:USERPROFILE\Desktop\asus-services-backup.csv" -NoTypeInformation -Encoding UTF8

# 2) 備份登錄檔
reg export "HKLM\SOFTWARE\ASUS" "$env:USERPROFILE\Desktop\asus-software.reg" /y

# 3) 備份排程工作
schtasks /query /tn "\ASUS\ArmourySocketServer" /xml > "$env:USERPROFILE\Desktop\task-ArmourySocketServer.xml"

# 4) 建還原點（管理員）
Checkpoint-Computer -Description "Before ASUS cleanup" -RestorePointType MODIFY_SETTINGS
```

還原：
```powershell
# Start 值：2=Auto, 3=Manual, 4=Disabled；延遲啟動另設
sc.exe config ASUSOptimization start= auto
sc.exe config ArmouryCrateService start= auto
schtasks /change /tn "\ASUS\ArmourySocketServer" /enable
```
最壞情況：重裝 `ASUSSystemControlInterfaceV3.exe` + Armoury Crate Installer，重開機即回原狀。

---

## 9. 我不確定的地方（別當定論）

1. **Armoury Crate Uninstall Tool 到底會不會順手移除 ASCI 驅動** — 官方文件只寫「all Armoury Crate related apps and services」，沒明講 SCI。**執行前先建還原點，執行後立刻 `sc query ATKWMIACPIIO`**。若被移除，改用官方 FAQ 1-6 的手動清單逐項移除。
2. **`ASUSOptimization` 開機時是否讀 `ChargingRate` 機碼來套 EC** — 我的推論，未證實。用 §5.2 Level 0 測就知道。
3. **`AsusMsControl` 的實際作用** — 查不到可靠說明，先別停。
4. **`ASUSSwitch` 停用對 FX608JMR 的 GPU 切換影響** — 機型相關，需自行測 MUX 切換。
5. **Windows Update 會不會把 AC App 本體裝回來** — 驅動幾乎一定會被更新；MSIX App 來自 Store，關掉 Store 自動更新可擋，但那是全域設定。

---

## 10. 建議路線（給你的情況）

你的工具以**一般使用者權限**直接寫 `\\.\ATKACPI`，跟 G-Helper 走同一條路，所以：

1. **先做 Level 2 的兩行**（停 `ArmouryCrateService` + 停 `ASUSOptimization`），測一輪重開機。
2. 若 Fn 熱鍵你不能沒有 → 復原 `ASUSOptimization`，改用 **Level 1 + 機碼改 0x50** 的折衷。
3. 確認新方案穩定後，再跑官方 Uninstall Tool 收尾（並立刻驗證 `ATKWMIACPIIO`）。
4. 全程**不要碰** `ATKWMIACPIIO`、`ACPI\ASUS2018`、`asussci2.inf`、`Asusgio3`。
