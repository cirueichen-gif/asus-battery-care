# ASUS TUF Gaming F16 FX608JMR：電池充電上限（Battery Care 80%）程式化控制

調查日期 2026-10-22。以下每一項都在「這台機器」上實測或直接讀原始碼，來源連結附在後面。

---

## 0. 結論（先看這段）

| 問題 | 答案 |
|---|---|
| Device_ID | **`0x00120057`**（Linux 核心名稱 `ASUS_WMI_DEVID_RSOC`，註解就叫 "Maximum charging percentage"）。**就是你在用的那個，沒錯。** |
| 寫入方式 | `DEVS(0x00120057, 60/80/100)`，回傳 **1 = 成功** |
| 讀回 | **沒有可靠的讀回路徑**。`DSTS(0x00120057)` 回的不是百分比。G-Helper 從不讀它；Linux 核心直接在原始碼寫明「DSDT 沒有可讀取 threshold 的方法，所以我們自己存」 |
| 介面選擇 | `\\.\ATKACPI` + IOCTL `0x0022240C` **優於** `root\wmi\AsusAtkWmi_WMNB` |
| 管理員 | **不需要**（本機實測非管理員可 open + DSTS + DEVS）。只有寫 registry 才要 |
| 會被覆蓋嗎 | **會**。AsusOptimization.exe 會在開機／滿電／MyASUS/Armoury Crate 操作時重寫 |

## 1. 實測結果（FX608JMR，非管理員身分）

我用 `CreateFile("\\.\ATKACPI")` + `DeviceIoControl(0x0022240C)` 直接打：

```
OPEN OK (non-admin) handle=1004
DSTS 0x00120057 -> 0x00890000 (8978432)      # RSOC / 充電上限
DSTS 0x0012006C -> 0x00010001 (65537)        # 充電模式，1 = 桶狀變壓器
VERS -> 0x0000003C

DEVS 0x00120057 = 80  ->  retval = 1  (OK)   # 非管理員寫入成功
```

**注意：同一支 `root\wmi` 路徑你量到 `0x00890800`，我走 ioctl 量到 `0x00890000`，差一個 bit 11。**
這正好證明**讀值不可信**——它不是「80%」也不是「60%」，只是狀態旗標。

### `0x00890000` 怎麼解讀

用 Linux 核心的 DSTS 遮罩定義：

| bit | 遮罩 | 本機值 | 意義 |
|---|---|---|---|
| 16 | `0x00010000` `PRESENCE_BIT` | **1** | **功能存在 / 支援** ✅ |
| 19 | `0x00080000` | 1 | 語意未文件化 |
| 23 | `0x00800000` | 1 | 語意未文件化 |
| 0–7 | `0x000000FF` | **0x00** | 若照別機型慣例是「上限值」→ 這裡是 0，**明顯不是 60/80/100** |

所以：`0x00890000 & 0x00010000 != 0` → **這台支援充電上限，設定走 `0x00120057` 沒錯**。
但低 byte 的 0 不代表「0% 限充」，只是這個 devid 沒有回讀語意。**不要拿 DSTS 當狀態顯示。**

### 掃過的相鄰 ID（`DSTS`，0x00120050–0x00120080）

只有這幾個有回應（其餘都是 `0xFFFFFFFE` / `0xFFFFFFFF` = 不支援）：

```
0x00120057 -> 0x00890000   ← 充電上限（RSOC）
0x00120059 -> 0x00010000
0x00120061 -> 0x00070001
0x0012006C -> 0x00010001   ← 充電模式（1=Barrel, 2=USB）
0x0012006D -> 0x00010000
0x0012006E -> 0x00010001
0x00120070 -> 0x00010000
0x00120075 -> 0x00010003   ← 效能模式（3 種）
0x0012007E -> 0x00010001
```

→ **`0x00120058`、`0x00120052`、`0x00120056` 都不存在**（你測到 `0xFFFFFFFE` 是對的，那是 `ASUS_WMI_UNSUPPORTED_METHOD`）。`0x0012006C` 是充電**模式**不是上限，別搞混。

## 2. G-Helper 怎麼做（原始碼，已 clone 驗證）

repo：<https://github.com/seerge/g-helper>（`app/AsusACPI.cs`、`app/Battery/BatteryControl.cs`）

### 常數

```csharp
// app/AsusACPI.cs:85
public const uint BatteryLimit = 0x00120057;

// app/AsusACPI.cs:58-61
public const int ChargerMode = 0x0012006C;
public const int ChargerUSB = 2;
public const int ChargerBarrel = 1;

// app/AsusACPI.cs:32-38
const string FILE_NAME = @"\\.\ATKACPI";
const uint CONTROL_CODE = 0x0022240C;
const uint DSTS = 0x53545344;   // "DSTS"
const uint DEVS = 0x53564544;   // "DEVS"
```

### 底層：先組 8 byte 標頭再送 IOCTL

```csharp
// app/AsusACPI.cs:401-416
protected byte[] CallMethod(uint MethodID, byte[] args)
{
    byte[] acpiBuf = new byte[8 + args.Length];
    byte[] outBuffer = new byte[16];

    BitConverter.GetBytes((uint)MethodID).CopyTo(acpiBuf, 0);   // "DEVS" / "DSTS"
    BitConverter.GetBytes((uint)args.Length).CopyTo(acpiBuf, 4); // args 長度
    Array.Copy(args, 0, acpiBuf, 8, args.Length);

    Control(CONTROL_CODE, acpiBuf, outBuffer);   // DeviceIoControl
    return outBuffer;
}

// app/AsusACPI.cs:432-445  —— 這就是「設上限」的全部
public int DeviceSet(uint DeviceID, int Status, string? logName)
{
    byte[] args = new byte[8];
    BitConverter.GetBytes((uint)DeviceID).CopyTo(args, 0);
    BitConverter.GetBytes((uint)Status).CopyTo(args, 4);

    byte[] status = CallMethod(DEVS, args);
    int result = BitConverter.ToInt32(status, 0);   // 1 = OK
    ...
    return result;
}

// app/AsusACPI.cs:481-489
public int DeviceGet(uint DeviceID)
{
    byte[] args = new byte[8];
    BitConverter.GetBytes((uint)DeviceID).CopyTo(args, 0);
    byte[] status = CallMethod(DSTS, args);
    return BitConverter.ToInt32(status, 0) - 65536;   // ← 可見 G-Helper 也只是「扣掉 presence bit」
}
```

### WMI 備援（跟你那條路一樣，含 `result` out 參數）

```csharp
// app/AsusACPI.cs:464-479
public static void DeviceSetWmi(uint DeviceID, int Status)
{
    using var wmi = new ManagementObjectSearcher(@"root\wmi",
        "SELECT * FROM AsusAtkWmi_WMNB").Get().Cast<ManagementObject>().First();
    var inParams = wmi.GetMethodParameters("DEVS");
    inParams["Device_ID"] = DeviceID;
    inParams["Control_status"] = (uint)Status;
    var result = Convert.ToInt32(wmi.InvokeMethod("DEVS", inParams, null)["result"]);
}
```

### 100 / 80 / 60 的實際寫法（含 registry 雙寫！）

```csharp
// app/Battery/BatteryControl.cs:30-35  —— 解除上限 = 寫 100
public static void SetBatteryLimitFull()
{
    chargeFull = true;
    Program.acpi.DeviceSet(AsusACPI.BatteryLimit, 100, "BatteryLimit");
}

// app/Battery/BatteryControl.cs:65-86  —— 設上限
public static void SetBatteryChargeLimit(int setLimit = -1)
{
    int limit = setLimit;
    if (limit < 0) limit = AppConfig.Get("charge_limit");
    if (limit < 40 || limit > 100) return;

    if (AppConfig.IsChargeLimit6080())          // ← 新機/部分 TUF 只吃 60/80/100
    {
        if (limit > 85) limit = 100;
        else if (limit >= 80) limit = 80;
        else if (limit < 60) limit = 60;
    }

    if (setLimit > 0) SetAsusChargeLimit(limit);          // ← 同步 registry（需管理員）

    Program.acpi.DeviceSet(AsusACPI.BatteryLimit, limit, "BatteryLimit");

    AppConfig.Set("charge_limit", limit);   // ← 自己存，因為讀不回來
    chargeFull = false;
}

// app/Battery/BatteryControl.cs:50-63  —— 這條是關鍵：同步給 ASUS 服務
public static void SetAsusChargeLimit(int value)
{
    if (!ProcessHelper.IsUserAdministrator()) return;
    const string keyPath =
        @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
    using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
    key?.SetValue("ChargingRate", value, RegistryValueKind.DWord);
}
```

### 啟動時重新套用（因為重開機會被 BIOS 洗掉）

```csharp
// app/Program.cs:497-504
int limit = AppConfig.Get("charge_limit");
acpi = new AsusACPI();
if (limit > 0 && limit < 100)
{
    if (acpi.IsConnected()) acpi.DeviceSet(AsusACPI.BatteryLimit, limit, "Limit");
    else AsusACPI.DeviceSetWmi(AsusACPI.BatteryLimit, limit);
}
```

### 對 2024+ 新機 / TUF 的特別處理

```csharp
// app/AppConfig.cs:770-773
public static bool IsChargeLimit6080()
{
    return ContainsModel("GU405") || ContainsModel("GU606") || ContainsModel("H760")
        || ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605")
        || ContainsModel("GA503R")
        || (IsTUF() && !(ContainsModel("FX507Z") || ContainsModel("FA617")
                         || ContainsModel("FA607") || ContainsModel("FX607")));
}

// app/AppConfig.cs:403-406
public static bool IsTUF() => ContainsModel("TUF") || ContainsModel("TX Gaming") || ContainsModel("TX Air");
```

**`FX608JMR` → `IsTUF()` = true，且不在排除名單 → 你的機器落在「60 / 80 / 100」這組。**
UI 滑桿給的是 `{60, 65, 70, 75, 80, 100}`（`app/Settings.cs:248`），但 `SetBatteryChargeLimit` 只把 `>85→100`、`80–85→80`、`<60→60`；**65/70/75 會原樣送去，韌體吃不吃不確定**（我沒測，別依賴）。

> 另外 `app/AppConfig.cs:517`、`:654` 都有 `FX608` 的專屬分支（`IsEcoBootFix`、`IsFanRequired`），確認 FX608 是它已知的機型。

## 3. 其他專案的做法

| 專案 | 平台 | 做法 |
|---|---|---|
| **Linux 核心 `asus-wmi`** | Linux | `ASUS_WMI_DEVID_RSOC = 0x00120057`，`asus_wmi_set_devstate(RSOC, value, &rv)`，檢查 `rv == 1`。**只寫不讀**：註解明寫「There isn't any method in the DSDT to read the threshold, so we save the threshold」，開機時還會主動寫一次 100 重置 |
| **asusctl / asusd** | Linux | 走上面的 sysfs `charge_control_end_threshold`，本質同一條 WMI |
| **pepa65/batlimit** | Linux | shell，直接 echo 進 `charge_control_end_threshold` |
| **Soap612/asus-battery-widget** | Windows | **跟你一樣的 `root\WMI\AsusAtkWmi_WMNB` DEVS `0x00120057`**，但 manifest 是 `requireAdministrator` |
| **AvikAgarwala/ASUS-Battery-Charging-Limit** | Windows | ❌ 走 `powercfg BATTERYTHRESHOLDSTART/STOP`，那是 Windows 通用電池閾值，**跟 ASUS 韌體無關**，別用 |
| **G-Helper** | Windows | 本報告主體 |

## 4. 真正的「另一條路」：registry（而且我找到證據了）

這台機器上：

```
HKLM\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys
    ChargingRate    REG_DWORD    0x50      # ← 80%，就是電池上限
    ChargingSmart   REG_DWORD    0x1
```

**誰在管這個值？** 我對 `AsusOptimization.exe`
（`C:\Windows\System32\DriverStore\FileRepository\asussci2.inf_amd64_197d319fab43e0fe\ASUSOptimization\`）
做字串掃描，它同時 import `RegNotifyChangeKeyValue`、`RegQueryValueEx`、`RegSetValueEx`、`DeviceIoControl`，而且內含：

```
\\.\ATKACPI
\\.\ASLDSRV
DeferredLimit from Boot / from SetInternal / from Toast / from FullCharge / from Reapply
SetChargingRateInternal / SetChargingRateFromToast / SetChargingRateFromReapply
SetChargingRateInRegistry MyASUS / SetChargingRateInRegistry Force / SetChargingRateInRegistry BIOS
ASUS Battery Health Charging UAP is installed
```

→ **`AsusOptimization.exe` 就是「Battery Care」的本體**：它監看 registry、透過 `\\.\ATKACPI` 下 ACPI 指令、開機時補套用，也會「從 BIOS / MyASUS / Force」把值寫回去。

**推論（未經完整驗證）**：改 `ChargingRate` 就可能等效於按 MyASUS 的 80%，而且能讓 AC/MyASUS 介面同步。但**我沒有實測「只寫 registry、不呼叫 DEVS」是否夠**——因為寫 HKLM 需要管理員，而我這個 session 不是管理員。**這點標記為「不確定」。**

## 5. 一定要管理員嗎？

- **`\\.\ATKACPI` ioctl：不用。** 本機實測非管理員 open + DSTS + DEVS 全通。G-Helper 的 manifest 是 `<requestedExecutionLevel level="asInvoker" />`，平常就是這樣跑的。
- **`root\wmi\AsusAtkWmi_WMNB`：要。** 跟你量到的一致（`Get-CimInstance` 被拒）。
- **寫 `HKLM\...\ChargingRate`：要。**（G-Helper 的 `SetAsusChargeLimit` 第一行就 `if (!IsUserAdministrator()) return;`）

→ **想免管理員：走 ioctl，別走 WMI。**

## 6. Armoury Crate 會不會改回去？會。

G-Helper 官方 FAQ 原文（<https://github.com/seerge/g-helper/wiki/FAQ>）：

> **Battery charge limiter is not working** — It could be that Asus services are overwriting this limit after. You can stop them from `Extra` settings by clicking `Stop` button. Some models support only 80% limit, so try to set exactly **80%**.
>
> After restart, BIOS resets all settings to defaults, including charge limit. As soon as you log in and G-Helper (or Asus service) starts it would apply charge limit again.
>
> **Can I use the MyASUS app along with G-Helper?** — You can, however MyASUS may override the battery charge limit that you set before. My advice would be to set the same limit in both.

處理方式（按侵入性排序）：

1. **雙寫**：`DEVS(0x00120057, 80)` **加** registry `ChargingRate = 80`（G-Helper 就是這樣，讓 AC/MyASUS 介面看到同一個值就不會反過來「修正」）。
2. **登入後重新套用**：工作排程器 / 開機腳本再打一次 DEVS（BIOS 一定會在重開機後洗回 100）。
3. **停掉 ASUS 服務**：`ASUSOptimization`、`AsusAppService`、`ArmouryCrateControlInterface`（G-Helper 的 Extra → Stop services 就是幹這個）。代價是 Fn 鍵/部分功能失效。
4. 直接改用 G-Helper 取代 Armoury Crate。

### ⚠️ 一個必須誠實講的插曲

我實測時 registry 是 `ChargingRate = 0x50`（80），但 **`Win32_Battery` 顯示 88%**——已經超過 80%，代表**那個 80 當時並沒有真的生效**。而在我的 `DEVS(0x00120057, 80)` 之後，registry 變成 **100**。

我**無法斷定因果**（可能是 AsusOptimization 的 `SetChargingRateInRegistry BIOS` 邏輯、可能是 AC 的「瞬滿」機制、也可能同一時間有人/程式在動 Armoury Crate）。但這正好是「值會被外部改寫」的活生生例子——**任何自寫方案都必須有「定期驗證 + 重套用」的機制**，不能寫一次就當沒事。

## 7. 可直接用的程式

### PowerShell（本機已驗證可讀；寫入回傳 1）

檔案：[Set-AsusBatteryLimit.ps1](Set-AsusBatteryLimit.ps1)

```powershell
.\Set-AsusBatteryLimit.ps1 -Show              # 看狀態
.\Set-AsusBatteryLimit.ps1 -Percent 80        # 只打 ACPI
.\Set-AsusBatteryLimit.ps1 -Percent 80 -SyncRegistry   # 另外同步 registry（要管理員）
```

核心就是：

```powershell
# 送 IOCTL 0x0022240C 到 \\.\ATKACPI
# in buffer: ["DEVS"(0x53564544)][len=8][Device_ID=0x00120057][Control_status=80]
# out buffer 第一個 Int32 == 1 → 成功
```

### C# 最小可用片段

```csharp
using System;
using System.Runtime.InteropServices;

const uint IOCTL_ATKACPI = 0x0022240C;
const uint DSTS = 0x53545344, DEVS = 0x53564544, RSOC = 0x00120057;

static IntPtr Open() => CreateFileW(@"\\.\ATKACPI",
    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
    IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

static int Call(IntPtr h, uint method, uint devId, uint arg)
{
    var inBuf  = new byte[16];
    var outBuf = new byte[16];
    BitConverter.GetBytes(method).CopyTo(inBuf, 0);
    BitConverter.GetBytes((uint)8).CopyTo(inBuf, 4);
    BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
    BitConverter.GetBytes(arg).CopyTo(inBuf, 12);
    uint ret;
    if (!DeviceIoControl(h, IOCTL_ATKACPI, inBuf, 16, outBuf, 16, out ret, IntPtr.Zero))
        return -1;
    return BitConverter.ToInt32(outBuf, 0);
}

// 設 80%
int r = Call(h, DEVS, RSOC, 80);   // r == 1 → OK
```

### WMI 版（要管理員，就是你在試的那條）

```powershell
$w = Get-CimInstance -Namespace root\wmi -ClassName AsusAtkWmi_WMNB
Invoke-CimMethod -InputObject $w -MethodName DEVS -Arguments @{
    Device_ID = [uint32]0x00120057
    Control_status = [uint32]80
}   # result == 1 → OK
```

## 8. 明確標記「不確定」的部分

1. **只寫 registry `ChargingRate` 是否足夠**（不呼叫 DEVS）：合理但**未實測**（寫 HKLM 需管理員，本 session 沒有）。
2. **65 / 70 / 75 這台吃不吃**：G-Helper 的 clamp 會放行，韌體是否 snap 到 60/80 未知。**要保守就用 60/80/100。**
3. **`0x00890000` 的 bit 19 / bit 23 意義**：無公開文件，不要依賴。
4. **registry 從 80 變 100 的原因**：只有觀察，沒有因果證明。
5. **AC 是否會在特定事件（滿電、插拔電、AC 更新）強制回寫 100**：機制存在（字串證據），觸發條件未逐一驗證。

## 來源

- G-Helper 原始碼：<https://github.com/seerge/g-helper>（`app/AsusACPI.cs` L32-38、L58-61、L85、L401-416、L432-489；`app/Battery/BatteryControl.cs` L30-86；`app/AppConfig.cs` L403-406、L770-773；`app/Program.cs` L497-504）
- G-Helper FAQ：<https://github.com/seerge/g-helper/wiki/FAQ>
- G-Helper issue #319（seerge 親自給的一行指令）：<https://github.com/seerge/g-helper/issues/319>
- Linux `include/linux/platform_data/x86/asus-wmi.h`：`ASUS_WMI_DEVID_RSOC 0x00120057`、DSTS 遮罩
- Linux `drivers/platform/x86/asus-wmi.c`：`charge_control_end_threshold_store` / `asus_wmi_battery_init`
- asus-linux（asusctl）：<https://asus-linux.org>
- Soap612/asus-battery-widget（Windows，WMI + requireAdministrator）
- pepa65/batlimit（Linux）
