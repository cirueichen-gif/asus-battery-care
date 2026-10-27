$src = @'
using System;
using System.Runtime.InteropServices;

public static class Atk
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint CONTROL_CODE = 0x0022240C;

    public static IntPtr Open(out int err)
    {
        IntPtr h = CreateFileW(@"\\.\ATKACPI", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        err = Marshal.GetLastWin32Error();
        return h;
    }

    public static int Call(IntPtr h, uint method, uint devId, uint arg)
    {
        byte[] inBuf = new byte[16];
        BitConverter.GetBytes(method).CopyTo(inBuf, 0);
        BitConverter.GetBytes((uint)8).CopyTo(inBuf, 4);
        BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
        BitConverter.GetBytes(arg).CopyTo(inBuf, 12);
        byte[] outBuf = new byte[16];
        uint returned;
        bool ok = DeviceIoControl(h, CONTROL_CODE, inBuf, 16, outBuf, 16, out returned, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        if (!ok) return unchecked((int)0xDEAD0000 | (err & 0xFFFF));
        return BitConverter.ToInt32(outBuf, 0);
    }

    public static void Close(IntPtr h) { CloseHandle(h); }
}
'@

Add-Type -TypeDefinition $src -Language CSharp

$err = 0
$h = [Atk]::Open([ref]$err)
if ($h -eq [IntPtr]::Zero -or $h -eq [IntPtr](-1)) {
    "OPEN FAILED, Win32Error=$err"
    exit
}
"OPEN OK (non-admin) handle=$h"

foreach ($id in 0x00120057, 0x0012006C, 0x0012005A) {
    $v = [Atk]::Call($h, 0x53545344, $id, 0)   # DSTS
    "DSTS 0x{0:X8} -> raw={1} (0x{1:X8})" -f $id, $v
}
# VERS to confirm the interface
"VERS -> 0x{0:X8}" -f [Atk]::Call($h, 0x53524556, 0, 0)
[Atk]::Close($h)
