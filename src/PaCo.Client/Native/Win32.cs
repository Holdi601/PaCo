using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaCo.Client.Native;

/// <summary>Win32 APIs for sessions, tokens, processes and desktops.</summary>
internal static class Win32
{
    // ---------- WTS (Terminal Services) ----------
    public static readonly IntPtr WtsCurrentServer = IntPtr.Zero;

    public enum WtsConnectState
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init,
    }

    public enum WtsInfoClass
    {
        WTSUserName = 5,
        WTSDomainName = 7,
        WTSConnectState = 8,
        WTSSessionInfoEx = 25,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WtsInfoExLevel1
    {
        public int SessionId;
        public WtsConnectState SessionState;
        public int SessionFlags; // WTS_SESSIONSTATE_LOCK = 0, UNLOCK = 1, UNKNOWN = -1
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public int IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WtsInfoEx
    {
        public int Level;
        public WtsInfoExLevel1 Data;
    }

    public const int WtsSessionStateLock = 0;
    public const int WtsSessionStateUnlock = 1;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(int sessionId, out SafeAccessTokenHandle token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);

    [DllImport("kernel32.dll")]
    public static extern int WTSGetActiveConsoleSessionId();

    // ---------- Tokens ----------
    public const uint TokenAssignPrimary = 0x0001, TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenAdjustDefault = 0x0080, TokenAdjustSessionId = 0x0100;
    public const uint MaximumAllowed = 0x02000000;

    public enum TokenInformationClass { TokenUser = 1, TokenSessionId = 12 }

    public enum SecurityImpersonationLevel { Anonymous, Identification, Impersonation, Delegation }

    public enum TokenType { Primary = 1, Impersonation = 2 }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        SecurityImpersonationLevel level, TokenType type, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(SafeAccessTokenHandle token, TokenInformationClass cls, ref int info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle token, TokenInformationClass cls, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    // ---------- Processes ----------
    public const uint CreateUnicodeEnvironment = 0x00000400, CreateNewProcessGroup = 0x00000200, DetachedProcess = 0x00000008;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string? application, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(int pid, out int sessionId);

    // ---------- Windows / input ----------
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, char[] text, int max);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public int cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Allows lower-integrity windows (explorer) to send this message to our SYSTEM process (tray clicks).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ChangeWindowMessageFilter(uint message, uint flag);

    public const uint MsgFltAdd = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    // ---------- Desktops ----------
    public const uint DesktopAllAccess = 0x01FF | 0x000F0000;
    public const uint DesktopSwitch = 0x0100, DesktopReadObjects = 0x0001;
    public const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr attributes);

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateDesktopW")]
    public static extern IntPtr CreateDesktopSecure(string name, IntPtr device, IntPtr devmode, int flags, uint access, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenDesktop(string name, int flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SwitchDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformation(IntPtr obj, int index, char[] info, int length, out int needed);

    public static string? DesktopName(IntPtr desk)
    {
        var buf = new char[256];
        return GetUserObjectInformation(desk, UoiName, buf, buf.Length * 2, out _) ? new string(buf).TrimEnd('\0') : null;
    }

    // ---------- Helpers ----------
    public static Win32Exception LastError(string what) => new(Marshal.GetLastWin32Error(), what);
}
