using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PaCo.Client.Native;

internal sealed record UserSession(int SessionId, string Sid, string UserName, bool Active, bool Locked);

/// <summary>Enumerates interactive user sessions (console, fast user switching, RDP) – SYSTEM only.</summary>
internal static class Sessions
{
    public static List<UserSession> Enumerate()
    {
        var result = new List<UserSession>();
        if (!Win32.WTSEnumerateSessions(Win32.WtsCurrentServer, 0, 1, out var ptr, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf<Win32.WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<Win32.WtsSessionInfo>(ptr + i * size);
                if (info.SessionId == 0) continue; // services session
                if (info.State is not (Win32.WtsConnectState.Active or Win32.WtsConnectState.Disconnected or Win32.WtsConnectState.Connected)) continue;
                var sid = SidOf(info.SessionId);
                if (sid is null) continue; // no user logged on (login screen)
                var (user, locked) = Details(info.SessionId);
                result.Add(new UserSession(info.SessionId, sid, user, info.State == Win32.WtsConnectState.Active, locked));
            }
        }
        finally
        {
            Win32.WTSFreeMemory(ptr);
        }
        return result;
    }

    public static string? SidOf(int sessionId)
    {
        if (!Win32.WTSQueryUserToken(sessionId, out var token)) return null;
        using (token) return SidOfToken(token);
    }

    public static string? SidOfToken(SafeAccessTokenHandle token)
    {
        Win32.GetTokenInformation(token, Win32.TokenInformationClass.TokenUser, IntPtr.Zero, 0, out var len);
        if (len <= 0) return null;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!Win32.GetTokenInformation(token, Win32.TokenInformationClass.TokenUser, buf, len, out _)) return null;
            var sidPtr = Marshal.ReadIntPtr(buf); // TOKEN_USER.User.Sid
            return new SecurityIdentifier(sidPtr).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static (string User, bool Locked) Details(int sessionId)
    {
        if (!Win32.WTSQuerySessionInformation(Win32.WtsCurrentServer, sessionId, Win32.WtsInfoClass.WTSSessionInfoEx, out var buf, out _))
            return ("", false);
        try
        {
            var ex = Marshal.PtrToStructure<Win32.WtsInfoEx>(buf);
            // Unknown (-1) is treated as unlocked so time is never silently not counted.
            return (ex.Data.UserName ?? "", ex.Data.SessionFlags == Win32.WtsSessionStateLock);
        }
        finally
        {
            Win32.WTSFreeMemory(buf);
        }
    }

    public static bool Logoff(int sessionId) => Win32.WTSLogoffSession(Win32.WtsCurrentServer, sessionId, false);
}
