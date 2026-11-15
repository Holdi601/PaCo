using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PaCo.Shared.I18n;

/// <summary>
/// Gettext-style translations: the German source text is the key. Each language is an embedded JSON file
/// "{prefix}.{code}.json" with { "German text": "translation" }. Missing entries fall back to English, then German.
/// Placeholders use .NET composite format ({0}, {1}).
/// </summary>
public sealed class Catalog
{
    private readonly Assembly _assembly;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Catalog(Assembly assembly, string resourcePrefix)
    {
        _assembly = assembly;
        _prefix = resourcePrefix;
    }

    public string Get(string language, string source)
    {
        if (language == Languages.Source || string.IsNullOrEmpty(source)) return source;
        if (Table(language).TryGetValue(source, out var t) && !string.IsNullOrWhiteSpace(t)) return t;
        if (language != "en" && Table("en").TryGetValue(source, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
        return source;
    }

    public string Format(string language, string source, params object?[] args)
    {
        var text = Get(language, source);
        if (args.Length == 0) return text;
        try
        {
            return string.Format(Languages.Culture(language), text, args);
        }
        catch (FormatException)
        {
            // A broken translation must never break the page – fall back to the source text.
            return string.Format(CultureInfo.InvariantCulture, source, args);
        }
    }

    public IReadOnlyDictionary<string, string> Table(string language) => _cache.GetOrAdd(language, Load);

    private IReadOnlyDictionary<string, string> Load(string language)
    {
        var name = $"{_prefix}.{language}.json";
        using var stream = _assembly.GetManifestResourceStream(name);
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
