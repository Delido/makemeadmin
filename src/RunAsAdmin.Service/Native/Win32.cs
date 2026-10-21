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
    // Fuer LoadUserProfile benoetigt (Registry-Hive laden): Backup + Restore.
    internal const string SE_BACKUP_NAME = "SeBackupPrivilege";
    internal const string SE_RESTORE_NAME = "SeRestorePrivilege";

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

    // TokenSessionId = 12 – damit der Prozess in der sichtbaren Sitzung startet.
    internal const int TokenSessionId = 12;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        ref uint TokenInformation, int TokenInformationLength);

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

    // --- Elevation-Status eines Prozesses prüfen ---
    internal const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION { public int TokenIsElevated; }

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

    // --- Benutzerprofil laden/entladen (für HKCU/AppData beim SYSTEM-Start) ---
    internal const int PROCESS_QUERY_INFORMATION = 0x0400;

    // LoadUserProfile: keine UI anzeigen (PI_NOUI).
    internal const int PI_NOUI = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROFILEINFO
    {
        public int dwSize;
        public int dwFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpUserName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProfilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDefaultPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpServerName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpPolicyPath;
        public IntPtr hProfile;
    }

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LoadUserProfile(IntPtr hToken, ref PROFILEINFO lpProfileInfo);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnloadUserProfile(IntPtr hToken, IntPtr hProfile);

    // --- Window-Station & Desktop DACL (für sichtbare Fenster des Kind-Prozesses) ---
    // Damit CreateProcessAsUser ein SICHTBARES Fenster erzeugen darf, muss das Token
    // des neuen Prozesses Zugriff auf die interaktive Window-Station winsta0 UND den
    // Desktop default haben (insb. DESKTOP_CREATEWINDOW). Dafür fügen wir deren DACL
    // Access-Allowed-ACEs hinzu.

    // Zugriffsrechte zum Öffnen der Window-Station / des Desktops (DACL ändern).
    internal const uint READ_CONTROL = 0x00020000;
    internal const uint WRITE_DAC = 0x00040000;

    // Window-Station: Vollzugriff (WINSTA_ALL_ACCESS) für die ACE.
    internal const uint WINSTA_ALL_ACCESS = 0x0000037F;

    // Desktop: Vollzugriff (DESKTOP_ALL_ACCESS) inkl. DESKTOP_CREATEWINDOW (0x0002).
    internal const uint DESKTOP_CREATEWINDOW = 0x0002;
    internal const uint DESKTOP_WRITEOBJECTS = 0x0080;
    internal const uint DESKTOP_ALL_ACCESS = 0x000201FF;

    // ACE-Vererbungsflags (für die Window-Station, damit Desktops erben).
    internal const byte CONTAINER_INHERIT_ACE = 0x02;
    internal const byte INHERIT_ONLY_ACE = 0x08;
    internal const byte OBJECT_INHERIT_ACE = 0x01;
    internal const byte NO_PROPAGATE_INHERIT_ACE = 0x04;

    // GetSecurityInfo/SetSecurityInfo: Objekttyp und betroffene Info.
    internal const int SE_WINDOW_OBJECT = 7;              // SE_OBJECT_TYPE
    internal const uint DACL_SECURITY_INFORMATION = 0x00000004;

    // ACL-Revision / ACE-Typ.
    internal const uint ACL_REVISION = 2;
    internal const int ACCESS_ALLOWED_ACE_TYPE = 0x0;

    // ACL-Information-Klasse.
    internal const int AclSizeInformation = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ACL_SIZE_INFORMATION
    {
        public uint AceCount;
        public uint AclBytesInUse;
        public uint AclBytesFree;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ACE_HEADER
    {
        public byte AceType;
        public byte AceFlags;
        public ushort AceSize;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenWindowStation(string lpszWinSta,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    // Sicherheitsinformationen eines Kernel-Objekts holen/setzen (advapi32).
    // GetSecurityInfo liefert die DACL als Zeiger in den SD (nicht freizugeben außer SD).
    [DllImport("advapi32.dll")]
    internal static extern uint GetSecurityInfo(IntPtr handle, int ObjectType,
        uint SecurityInfo, IntPtr ppsidOwner, IntPtr ppsidGroup,
        out IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll")]
    internal static extern uint SetSecurityInfo(IntPtr handle, int ObjectType,
        uint SecurityInfo, IntPtr psidOwner, IntPtr psidGroup,
        IntPtr pDacl, IntPtr pSacl);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // --- ACL-Aufbau (neue DACL mit zusätzlicher ACE zusammensetzen) ---
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeAcl(IntPtr pAcl, uint nAclLength, uint dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetAclInformation(IntPtr pAcl, ref ACL_SIZE_INFORMATION pAclInformation,
        uint nAclInformationLength, int dwAclInformationClass);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetAce(IntPtr pAcl, uint dwAceIndex, out IntPtr pAce);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddAce(IntPtr pAcl, uint dwAceRevision, uint dwStartingAceIndex,
        IntPtr pAceList, uint nAceListLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddAccessAllowedAceEx(IntPtr pAcl, uint dwAceRevision,
        uint AceFlags, uint AccessMask, byte[] pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsValidAcl(IntPtr pAcl);

    internal enum SID_NAME_USE { User = 1, Group, Domain, Alias, WellKnownGroup, DeletedAccount, Invalid, Unknown, Computer, Label }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupAccountSid(string? systemName, byte[] sid,
        System.Text.StringBuilder name, ref int cchName,
        System.Text.StringBuilder referencedDomainName, ref int cchReferencedDomainName,
        out SID_NAME_USE use);
}
