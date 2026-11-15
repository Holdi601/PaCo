using System.Globalization;
using PaCo.Shared.I18n;

namespace PaCo.Client;

/// <summary>
/// Client UI texts. German source text is the key; translations in embedded "I18n/client.{lang}.json".
/// Setup uses the Windows display language of the admin; child-facing texts use the child's language from the policy.
/// </summary>
internal static class L
{
    public static readonly Catalog Catalog = new(typeof(L).Assembly, "PaCo.Client.I18n.client");

    /// <summary>Current UI language of this process.</summary>
    public static string Lang { get; set; } = Languages.Normalize(CultureInfo.CurrentUICulture.Name);

    public static string T(string german) => Catalog.Get(Lang, german);

    public static string F(string german, params object?[] args) => Catalog.Format(Lang, german, args);

    /// <summary>Text in a specific language (e.g. the child's).</summary>
    public static string In(string lang, string german, params object?[] args) =>
        args.Length == 0 ? Catalog.Get(lang, german) : Catalog.Format(lang, german, args);
}
