using System.Globalization;

namespace PaCo.Shared.I18n;

/// <summary>
/// Texts used by both server and client: device events, security findings, alert categories, push messages,
/// help hints. Source language German; translations in embedded "I18n/shared.{lang}.json".
/// </summary>
public static class SharedText
{
    public static readonly Catalog Catalog = new(typeof(SharedText).Assembly, "PaCo.Shared.I18n.shared");

    public static string T(string lang, string german) => Catalog.Get(lang, german);

    public static string F(string lang, string germanTemplate, params object?[] args) => Catalog.Format(lang, germanTemplate, args);

    /// <summary>Formats a template with string args in German (for the stored fallback text).</summary>
    public static string German(string template, IReadOnlyList<string> args) =>
        args.Count == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args.Cast<object?>().ToArray());

    /// <summary>Translated event/finding text: template if present, else the stored German text.</summary>
    public static string Render(string lang, string? template, IReadOnlyList<string> args, string fallback) =>
        template is null ? Catalog.Get(lang, fallback) : Catalog.Format(lang, template, args.Cast<object?>().ToArray());
}
