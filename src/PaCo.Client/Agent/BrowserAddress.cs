using System.Windows.Automation;
using PaCo.Shared;

namespace PaCo.Client.Agent;

/// <summary>
/// Reads the address of the active tab from the browser's address bar via UI Automation, for per-site time limits and
/// learning sites. Chromium browsers (Edge, Chrome, Brave, Opera, Vivaldi …): the first edit field is the omnibox.
/// Firefox: the field with AutomationId "urlbar-input".
/// </summary>
internal static class BrowserAddress
{
    private static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase)
        { "msedge", "chrome", "brave", "opera", "opera_gx", "vivaldi", "chromium", "yandex", "browser" };

    private static readonly HashSet<string> Gecko = new(StringComparer.OrdinalIgnoreCase)
        { "firefox", "waterfox", "librewolf", "floorp" };

    public static bool IsBrowser(string process) => Chromium.Contains(process) || Gecko.Contains(process);

    /// <summary>Host of the active tab, or null (not a browser, address not readable, or a search being typed).</summary>
    public static string? ReadHost(IntPtr hwnd, string process)
    {
        if (!IsBrowser(process)) return null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var condition = Gecko.Contains(process)
                ? new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input")
                : (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var edit = root.FindFirst(TreeScope.Descendants, condition);
            if (edit is null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) return null;
            // While the address bar has the keyboard focus the child may be typing – that text is not the open page.
            if (edit.Current.HasKeyboardFocus) return null;
            return TimeGroups.HostOf(((ValuePattern)vp).Current.Value);
        }
        catch
        {
            return null; // window closed meanwhile, browser busy, …
        }
    }
}
