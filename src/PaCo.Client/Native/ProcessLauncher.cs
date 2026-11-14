using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace PaCo.Client.Native;

/// <summary>Starts a process as SYSTEM inside a user's session (so the child cannot end it).</summary>
internal static class ProcessLauncher
{
    public static Process StartAsSystemInSession(int sessionId, string exe, string arguments)
    {
        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(),
                Win32.TokenDuplicate | Win32.TokenQuery | Win32.TokenAssignPrimary | Win32.TokenAdjustDefault | Win32.TokenAdjustSessionId,
                out var own))
            throw Win32.LastError("OpenProcessToken");
        using (own)
        {
            if (!Win32.DuplicateTokenEx(own, Win32.MaximumAllowed, IntPtr.Zero, Win32.SecurityImpersonationLevel.Impersonation, Win32.TokenType.Primary, out var dup))
                throw Win32.LastError("DuplicateTokenEx");
            using (dup)
            {
                var sid = sessionId;
                if (!Win32.SetTokenInformation(dup, Win32.TokenInformationClass.TokenSessionId, ref sid, sizeof(int)))
                    throw Win32.LastError("SetTokenInformation(SessionId)");
                return Start(dup, exe, arguments);
            }
        }
    }

    private static Process Start(SafeAccessTokenHandle token, string exe, string arguments)
    {
        var si = new Win32.StartupInfo
        {
            cb = System.Runtime.InteropServices.Marshal.SizeOf<Win32.StartupInfo>(),
            lpDesktop = @"winsta0\default",
        };
        var cmd = $"\"{exe}\" {arguments}";
        if (!Win32.CreateProcessAsUser(token, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, Win32.CreateUnicodeEnvironment, IntPtr.Zero,
                Path.GetDirectoryName(exe), ref si, out var pi))
            throw Win32.LastError("CreateProcessAsUser");
        Win32.CloseHandle(pi.hThread);
        try
        {
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            Win32.CloseHandle(pi.hProcess);
        }
    }
}
