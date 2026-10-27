<#
.SYNOPSIS
    ASUS TUF/ROG 電池充電上限（Battery Care / RSOC）讀寫工具
.DESCRIPTION
    走 \\.\ATKACPI 的 DEVS/DSTS（IOCTL 0x0022240C），Device_ID = 0x00120057
    實測 FX608JMR (2025 TUF F16) 非管理員即可寫入。
.NOTES
    - 讀回值不可信（BIOS 沒有回讀介面）。真正的狀態請看 registry ChargingRate。
    - 重開機 BIOS 會把上限重設成 100，需在登入後重新套用。
.EXAMPLE
    .\Set-AsusBatteryLimit.ps1 -Get
    .\Set-AsusBatteryLimit.ps1 -Percent 80
#>
[CmdletBinding(DefaultParameterSetName = 'Get')]
param(
    [Parameter(ParameterSetName = 'Set')]
    [ValidateSet(60, 80, 100)]
    [int]$Percent,

    [Parameter(ParameterSetName = 'Get')]
    [switch]$Show,

    # 同時把值寫進 ASUS System Control Interface 的 registry（讓 AsusOptimization 服務同步）
    [switch]$SyncRegistry
)

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class AsusAtk
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share,
        IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize,
        byte[] outBuf, uint outSize, out uint returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint IOCTL_ATKACPI = 0x0022240C;
    const uint DSTS = 0x53545344;  // "DSTS"
    const uint DEVS = 0x53564544;  // "DEVS"

    static IntPtr _h = IntPtr.Zero;

    static IntPtr Handle()
    {
        if (_h != IntPtr.Zero) return _h;
        _h = CreateFileW(@"\\.\ATKACPI", 0x80000000 | 0x40000000, 1 | 2,
            IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        return _h;
    }

    public static bool IsOpen { get { return Handle() != new IntPtr(-1) && Handle() != IntPtr.Zero; } }

    static int Call(uint method, uint devId, uint arg)
    {
        IntPtr h = Handle();
        if (h == new IntPtr(-1) || h == IntPtr.Zero) return -1;

        byte[] inBuf = new byte[16];
        byte[] outBuf = new byte[16];
        BitConverter.GetBytes(method).CopyTo(inBuf, 0);
        BitConverter.GetBytes((uint)8).CopyTo(inBuf, 4);       // args 長度 = 2 個 dword
        BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
        BitConverter.GetBytes(arg).CopyTo(inBuf, 12);

        uint returned;
        if (!DeviceIoControl(h, IOCTL_ATKACPI, inBuf, 16, outBuf, 16, out returned, IntPtr.Zero))
            return -1;
        return BitConverter.ToInt32(outBuf, 0);
    }

    public static int Dsts(uint devId) { return Call(DSTS, devId, 0); }
    public static int Devs(uint devId, uint value) { return Call(DEVS, devId, value); }
}
'@

if (-not ('AsusAtk' -as [type])) { Add-Type -TypeDefinition $sig -Language CSharp }

$DEVID_RSOC = 0x00120057
$RegPath = 'HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys'

if (-not [AsusAtk]::IsOpen) {
    Write-Error '無法開啟 \\.\ATKACPI。請確認 ATKACPI 驅動（ASUS System Control Interface）已安裝。'
    return
}

if ($PSCmdlet.ParameterSetName -eq 'Get') {
    $raw = [uint32][AsusAtk]::Dsts($DEVID_RSOC)
    $reg = (Get-ItemProperty -Path $RegPath -ErrorAction SilentlyContinue).ChargingRate
    [pscustomobject]@{
        DeviceId_STS     = ('0x{0:X8}' -f $raw)
        Supported        = (($raw -band 0x00010000) -ne 0)
        ReadValueLowByte = ($raw -band 0xFF)          # 無意義，只供觀察
        ChargingRate_Reg = $reg                        # 真正的可信來源
        BatteryPercent   = (Get-CimInstance Win32_Battery).EstimatedChargeRemaining
    } | Format-List
    return
}

# ---- 寫入 ----
$ret = [AsusAtk]::Devs($DEVID_RSOC, [uint32]$Percent)
if ($ret -eq 1) {
    Write-Host "DEVS(0x00120057, $Percent) -> OK" -ForegroundColor Green
} else {
    Write-Warning "DEVS 回傳 $ret（1 = 成功；其他 = BIOS 拒絕/不支援）"
}

if ($SyncRegistry) {
    try {
        Set-ItemProperty -Path $RegPath -Name 'ChargingRate' -Value $Percent -Type DWord -ErrorAction Stop
        Write-Host "registry ChargingRate = $Percent" -ForegroundColor Green
    } catch {
        Write-Warning "registry 寫入失敗（需管理員）：$($_.Exception.Message)"
    }
}

# ------------------------------------------------------------------
# C# 版最小可用片段（G-Helper 就是這樣做的）：
#
#   var args = new byte[8];
#   BitConverter.GetBytes(0x00120057u).CopyTo(args, 0);
#   BitConverter.GetBytes((uint)80).CopyTo(args, 4);
#   // 前 4 byte 放方法 ID "DEVS" (0x53564544)，接著 4 byte args 長度，再來才是 args
#   // 送 IOCTL 0x0022240C 到 \\.\ATKACPI，取回傳 buffer 第一個 int，== 1 代表成功
# ------------------------------------------------------------------
