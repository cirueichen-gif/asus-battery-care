$src = @'
using System;
using System.Runtime.InteropServices;
public static class Atk3
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr ov);
    public static IntPtr Open() { return CreateFileW(@"\\.\ATKACPI", 0x80000000 | 0x40000000, 1 | 2, IntPtr.Zero, 3, 0x80, IntPtr.Zero); }
    public static int Dsts(IntPtr h, uint devId)
    {
        byte[] inBuf = new byte[16]; byte[] outBuf = new byte[16]; uint returned;
        BitConverter.GetBytes((uint)0x53545344).CopyTo(inBuf, 0);
        BitConverter.GetBytes((uint)8).CopyTo(inBuf, 4);
        BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
        if (!DeviceIoControl(h, 0x0022240C, inBuf, 16, outBuf, 16, out returned, IntPtr.Zero)) return -1;
        return BitConverter.ToInt32(outBuf, 0);
    }
}
'@
Add-Type -TypeDefinition $src -Language CSharp
$h = [Atk3]::Open()
if ($h -eq [IntPtr](-1)) { "OPEN FAILED"; exit }
"scan 0x00120050 - 0x00120080 (DSTS read-only)"
for ($i = 0x50; $i -le 0x80; $i++) {
    $id = 0x00120000 + $i
    $v = [Atk3]::Dsts($h, [uint32]$id)
    if ($v -ne -2) { "0x{0:X8} -> 0x{1:X8} ({1})" -f $id, ($v -band 0xFFFFFFFFL) }
}
"--- selected extras ---"
foreach ($id in 0x00120091, 0x00120056, 0x00120058, 0x0012005B, 0x0012006D, 0x0012006E) {
    $v = [Atk3]::Dsts($h, [uint32]$id)
    "0x{0:X8} -> 0x{1:X8}" -f $id, ($v -band 0xFFFFFFFFL)
}
"--- registry re-read x3 ---"
for ($k = 0; $k -lt 3; $k++) {
    (Get-ItemProperty "HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys").ChargingRate
    Start-Sleep -Seconds 3
}
"--- charge now ---"
(Get-CimInstance Win32_Battery).EstimatedChargeRemaining
(Get-Service ArmouryCrateService -ErrorAction SilentlyContinue).Status
