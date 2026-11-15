using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;
using PaCo.Shared.I18n;

namespace PaCo.Server;

/// <summary>
/// Website texts. German source text is the key; translations in embedded "I18n/server.{lang}.json".
/// The request language comes from <see cref="CultureInfo.CurrentUICulture"/> (set by the request localization
/// middleware from the language cookie or the browser's Accept-Language). Razor: <c>@T("…")</c>, <c>@F("…{0}", x)</c>.
/// </summary>
public static class Ui
{
    public static readonly Catalog Catalog = new(typeof(Ui).Assembly, "PaCo.Server.I18n.server");

    /// <summary>Cookie holding the chosen language (also set at sign-in from the parent's saved choice).</summary>
    public const string LangCookie = "__Host-paco-lang";

    public static string Lang => Languages.Normalize(CultureInfo.CurrentUICulture.Name);

    public static string T(string german) => Catalog.Get(Lang, german);

    public static string F(string german, params object?[] args) => Catalog.Format(Lang, german, args);

    /// <summary>Texts shared with the client (events, findings, alert categories) in the current language.</summary>
    public static string S(string german) => SharedText.T(Lang, german);

    public static string Render(string? template, IReadOnlyList<string> args, string fallback) => SharedText.Render(Lang, template, args, fallback);

    /// <summary>Text in a specific language (e.g. push notifications in the family's language).</summary>
    public static string In(string lang, string german, params object?[] args) =>
        args.Length == 0 ? Catalog.Get(lang, german) : Catalog.Format(lang, german, args);

    public static void SetLanguageCookie(HttpResponse response, string lang) =>
        response.Cookies.Append(LangCookie, CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(Languages.Culture(lang))),
            new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true, MaxAge = TimeSpan.FromDays(400) });

    public static RequestLocalizationOptions LocalizationOptions()
    {
        var cultures = Languages.All.Select(l => Languages.Culture(l.Code)).ToList();
        var o = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(Languages.Culture("en")),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
            FallBackToParentCultures = true,
            FallBackToParentUICultures = true,
        };
        o.RequestCultureProviders =
        [
            new AccountLanguageProvider(),
            new CookieRequestCultureProvider { CookieName = LangCookie },
            new NormalizingAcceptLanguageProvider(),
        ];
        return o;
    }

    /// <summary>Language saved in the parent's account (claim, refreshed at sign-in and when changed).</summary>
    private sealed class AccountLanguageProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            var lang = httpContext.User.FindFirst(Services.PaCoClaims.Language)?.Value;
            if (!Languages.IsSupported(lang)) return NullProviderCultureResult;
            var culture = Languages.Culture(lang!).Name;
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture, culture));
        }
    }

    /// <summary>Maps browser languages like "zh-TW", "nn-NO" or "de-AT" onto the supported set.</summary>
    private sealed class NormalizingAcceptLanguageProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            var header = httpContext.Request.GetTypedHeaders().AcceptLanguage;
            foreach (var v in header.OrderByDescending(h => h.Quality ?? 1))
            {
                var tag = v.Value.Value;
                if (string.IsNullOrEmpty(tag) || tag == "*") continue;
                var code = Languages.Normalize(tag);
                // Normalize maps unknown languages to English – only accept that for real English tags.
                if (code == "en" && !tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) continue;
                var culture = Languages.Culture(code).Name;
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture, culture));
            }
            return NullProviderCultureResult;
        }
    }
}

/// <summary>Translates DataAnnotations error messages (German keys) via the website catalog.</summary>
public sealed class CatalogStringLocalizer : IStringLocalizer
{
    public LocalizedString this[string name] => new(name, Ui.T(name), resourceNotFound: false);

    public LocalizedString this[string name, params object[] arguments] => new(name, Ui.F(name, arguments), resourceNotFound: false);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
