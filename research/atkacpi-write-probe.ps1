$src = @'
using System;
using System.Runtime.InteropServices;
public static class Atk2
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr ov);
    const uint CONTROL_CODE = 0x0022240C;
    public static IntPtr Open()
    {
        return CreateFileW(@"\\.\ATKACPI", 0x80000000 | 0x40000000, 1 | 2, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
    }
    public static int Call(IntPtr h, uint method, uint devId, uint arg, out uint returned)
    {
        byte[] inBuf = new byte[16];
        BitConverter.GetBytes(method).CopyTo(inBuf, 0);
        BitConverter.GetBytes((uint)8).CopyTo(inBuf, 4);
        BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
        BitConverter.GetBytes(arg).CopyTo(inBuf, 12);
        byte[] outBuf = new byte[16];
        bool ok = DeviceIoControl(h, CONTROL_CODE, inBuf, 16, outBuf, 16, out returned, IntPtr.Zero);
        if (!ok) return -1;
        return BitConverter.ToInt32(outBuf, 0);
    }
}
'@
Add-Type -TypeDefinition $src -Language CSharp

$h = [Atk2]::Open()
if ($h -eq [IntPtr](-1)) { "OPEN FAILED"; exit }
$ret = 0
"--- DEVS 0x00120057 = 80 (idempotent, same as current registry value) ---"
$r = [Atk2]::Call($h, 0x53564544, 0x00120057, 80, [ref]$ret)
"DEVS retval=$r  bytesReturned=$ret"
Start-Sleep -Milliseconds 300
$r2 = [Atk2]::Call($h, 0x53545344, 0x00120057, 0, [ref]$ret)
"DSTS after write -> 0x{0:X8} ({0})" -f $r2
"--- registry now ---"
(Get-ItemProperty "HKLM:\SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys").ChargingRate
"--- battery ---"
Get-CimInstance Win32_Battery | Select-Object EstimatedChargeRemaining, BatteryStatus, DesignCapacity, FullChargeCapacity | Format-List
