using System.Runtime.InteropServices;

namespace RunAsAdmin.Service.Native;

/// <summary>
/// P/Invoke-Deklarationen für den erhöhten Start eines Prozesses in der
/// interaktiven Sitzung des angemeldeten Benutzers. Nur unter Windows.
/// </summary>
internal static partial class Win32
{
    internal const int TOKEN_DUPLICATE = 0x0002;
    internal const int TOKEN_QUERY = 0x0008;
    internal const int TOKEN_ASSIGN_PRIMARY = 0x0001;
    internal const int TOKEN_ADJUST_DEFAULT = 0x0080;
    internal const int TOKEN_ADJUST_SESSIONID = 0x0100;
    internal const uint MAXIMUM_ALLOWED = 0x02000000;

    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;
    internal const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    internal enum SECURITY_IMPERSONATION_LEVEL { Anonymous, Identification, Impersonation, Delegation }
    internal enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    // --- Privilege-Aktivierung (SeTcbPrivilege für WTSQueryUserToken) ---
    internal const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const int SE_PRIVILEGE_ENABLED = 0x00000002;
    internal const string SE_TCB_NAME = "SeTcbPrivilege";
    internal const string SE_ASSIGNPRIMARYTOKEN_NAME = "SeAssignPrimaryTokenNameTokenPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES { public LUID Luid; public int Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr ProcessHandle, int DesiredAccess, out IntPtr TokenHandle);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    // TOKEN_INFORMATION_CLASS.TokenLinkedToken
    internal const int TokenLinkedToken = 19;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_LINKED_TOKEN { public IntPtr LinkedToken; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WTSGetActiveConsoleSessionId();

    // --- Sitzungsaufzählung (robustere Ermittlung der aktiven Benutzersitzung) ---
    internal enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected,
        WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr pMemory);

    internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType, out IntPtr phNewToken);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName,
        string? lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    /// <summary>Beendet eine Impersonation auf dem aktuellen Thread (zurück zu SYSTEM).</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RevertToSelf();

    // --- Lokale Gruppen über native netapi32 (robust auch bei Microsoft-Konten) ---
    [StructLayout(LayoutKind.Sequential)]
    internal struct LOCALGROUP_MEMBERS_INFO_0 { public IntPtr lgrmi0_sid; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupAddMembers(string? servername, string groupname,
        int level, ref LOCALGROUP_MEMBERS_INFO_0 buf, int totalentries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupDelMembers(string? servername, string groupname,
        int level, ref LOCALGROUP_MEMBERS_INFO_0 buf, int totalentries);

    // Rückgabewerte
    internal const int NERR_Success = 0;
    internal const int ERROR_MEMBER_IN_ALIAS = 1378; // schon Mitglied
    internal const int ERROR_NO_SUCH_MEMBER = 1387;
    internal const int ERROR_MEMBER_NOT_IN_ALIAS = 1377;

    internal enum SID_NAME_USE { User = 1, Group, Domain, Alias, WellKnownGroup, DeletedAccount, Invalid, Unknown, Computer, Label }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupAccountSid(string? systemName, byte[] sid,
        System.Text.StringBuilder name, ref int cchName,
        System.Text.StringBuilder referencedDomainName, ref int cchReferencedDomainName,
        out SID_NAME_USE use);
}
