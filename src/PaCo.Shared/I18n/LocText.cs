using System.Globalization;

namespace PaCo.Shared.I18n;

/// <summary>
/// A translatable text: German composite-format template (the catalog key) plus pre-formatted string arguments.
/// Stored with events and findings so the website can show them in each parent's language.
/// </summary>
public sealed record LocText(string Template, IReadOnlyList<string> Args)
{
    public static LocText Of(string template, params object?[] args) => new(template, args.Select(Arg).ToArray());

    /// <summary>German text (fallback, logs).</summary>
    public string German => SharedText.German(Template, Args);

    public string In(string lang) => SharedText.Render(lang, Template, Args, German);

    public override string ToString() => German;

    private static string Arg(object? o) => o switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "",
    };
}
