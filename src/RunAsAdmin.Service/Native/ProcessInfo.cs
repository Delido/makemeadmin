using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static RunAsAdmin.Service.Native.Win32;

namespace RunAsAdmin.Service.Native;

/// <summary>Prüft, ob ein Prozess erhöht (als Administrator) läuft.</summary>
[SupportedOSPlatform("windows")]
internal static class ProcessInfo
{
    public static bool IsElevated(int pid)
    {
        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_QUERY, out var hToken)) return false;
            try
            {
                var size = Marshal.SizeOf<TOKEN_ELEVATION>();
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetTokenInformation(hToken, TokenElevation, buf, size, out _))
                    {
                        var elev = Marshal.PtrToStructure<TOKEN_ELEVATION>(buf);
                        return elev.TokenIsElevated != 0;
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(hToken); }
        }
        catch { return false; }
        finally { CloseHandle(hProc); }
    }
}
