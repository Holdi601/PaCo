using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaCo.Server;
using PaCo.Server.Api;
using PaCo.Server.Data;
using PaCo.Server.Services;

if (args.Length > 0 && AdminCli.IsCommand(args[0]))
    return await AdminCli.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);
var opts = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
Directory.CreateDirectory(opts.DataDir);

builder.Services.AddSingleton(TimeProvider.System);
// Device API must use exactly the same JSON settings as the client (PaCoJson), e.g. enums as strings.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Clear();
    foreach (var c in PaCo.Shared.PaCoJson.Options.Converters) o.SerializerOptions.Converters.Add(c);
    o.SerializerOptions.DefaultIgnoreCondition = PaCo.Shared.PaCoJson.Options.DefaultIgnoreCondition;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IFamilyContext, HttpFamilyContext>();
builder.Services.AddDbContext<PaCoDb>(o => o.UseSqlite($"Data Source={Path.Combine(opts.DataDir, "paco.db")}"));
builder.Services.AddSingleton<SigningKeyStore>();
builder.Services.AddSingleton<ClientPackage>();
builder.Services.AddSingleton<TlsCertificateProvider>();
builder.Services.AddScoped<PolicyBuilder>();
builder.Services.AddScoped<ParentActions>();
builder.Services.AddScoped<AccountAdmin>();
builder.Services.AddHttpClient("github", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<GitHubUpdates>();
builder.Services.AddHttpClient("ntfy", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<ScreenshotStore>();
builder.Services.AddHostedService<Housekeeping>();
builder.Services.AddScoped<FamilyInfo>();
builder.Services.AddHttpClient("wifi-geo", c => c.Timeout = TimeSpan.FromSeconds(6));
builder.Services.AddSingleton<WifiGeolocator>();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = AccountAdmin.InviteLifetime);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(opts.DataDir, "dp-keys")))
    .SetApplicationName("PaCo");

builder.Services.AddIdentity<ParentUser, IdentityRole<Guid>>(o =>
    {
        o.Password.RequiredLength = 12;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireDigit = true;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<PaCoDb>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<PaCoClaimsFactory>()
    .AddErrorDescriber<GermanIdentityErrors>();

builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "__Host-paco";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
});
builder.Services.ConfigureExternalCookie(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, o =>
{
    o.Cookie.Name = "__Host-paco-2fa";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(PaCoPolicies.Parent, p => p.RequireAuthenticatedUser()
        .RequireClaim(PaCoClaims.Family)
        .RequireClaim(PaCoClaims.TwoFactor, "1")
        .RequireClaim(PaCoClaims.MustChangePassword, "0"))
    .AddPolicy(PaCoPolicies.SuperAdmin, p => p.RequireAuthenticatedUser()
        .RequireRole(PaCoRoles.SuperAdmin)
        .RequireClaim(PaCoClaims.TwoFactor, "1")
        .RequireClaim(PaCoClaims.MustChangePassword, "0"));

builder.Services.AddRazorPages(o =>
{
    // Parent area (family data): password changed + 2FA active + family.
    o.Conventions.AuthorizePage("/Dashboard", PaCoPolicies.Parent);
    o.Conventions.AuthorizeFolder("/Children", PaCoPolicies.Parent);
    o.Conventions.AuthorizeFolder("/Devices", PaCoPolicies.Parent);
    o.Conventions.AuthorizePage("/Alerts", PaCoPolicies.Parent);
    o.Conventions.AuthorizePage("/Notifications", PaCoPolicies.Parent);
    o.Conventions.AuthorizePage("/Family", PaCoPolicies.Parent);
    // Super admin area: account management only, no family data.
    o.Conventions.AuthorizeFolder("/Admin", PaCoPolicies.SuperAdmin);
    // Account pages: signed in (onboarding middleware handles password/2FA setup) …
    o.Conventions.AuthorizeFolder("/Account");
    // … except the sign-in flow itself.
    foreach (var p in new[] { "/Account/Login", "/Account/LoginWith2fa", "/Account/LoginWithRecoveryCode", "/Account/AcceptInvite", "/Account/AccessDenied", "/Account/Logout" })
        o.Conventions.AllowAnonymousToPage(p);
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/Index"); // Only redirects (login / dashboard / admin).
}).AddMvcOptions(o =>
{
    // Form errors the parents may see (e.g. a time typed wrongly) – in German instead of the English defaults.
    var m = o.ModelBindingMessageProvider;
    m.SetValueIsInvalidAccessor(v => $"Der Wert „{v}“ ist ungültig.");
    m.SetAttemptedValueIsInvalidAccessor((v, f) => $"Der Wert „{v}“ ist ungültig.");
    m.SetNonPropertyAttemptedValueIsInvalidAccessor(v => $"Der Wert „{v}“ ist ungültig.");
    m.SetUnknownValueIsInvalidAccessor(f => "Ungültige Eingabe.");
    m.SetNonPropertyUnknownValueIsInvalidAccessor(() => "Ungültige Eingabe.");
    m.SetValueMustNotBeNullAccessor(v => "Bitte ausfüllen.");
    m.SetMissingBindRequiredValueAccessor(f => "Bitte ausfüllen.");
    m.SetMissingKeyOrValueAccessor(() => "Bitte ausfüllen.");
    m.SetMissingRequestBodyRequiredValueAccessor(() => "Bitte ausfüllen.");
    m.SetValueMustBeANumberAccessor(f => "Bitte eine Zahl eingeben.");
    m.SetNonPropertyValueMustBeANumberAccessor(() => "Bitte eine Zahl eingeben.");
});
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "__Host-paco-af";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy(DeviceApi.RateLimitPair, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
    o.AddPolicy(DeviceApi.RateLimitDevice, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(AccountRateLimit.Name, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = opts.AccountRateLimit, Window = TimeSpan.FromMinutes(5) }));
});

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = false;
});

var tls = new TlsCertificateProvider(Microsoft.Extensions.Options.Options.Create(opts),
    LoggerFactory.Create(l => l.AddConsole()).CreateLogger<TlsCertificateProvider>());
if (tls.Enabled)
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
        k.ListenAnyIP(opts.HttpsPort, l => l.UseHttps(h => h.ServerCertificateSelector = (_, _) => tls.Current));
    });
}
else
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
    await db.Database.MigrateAsync();
    _ = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseSecurityHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseOnboardingRedirect();
app.UseAuthorization();

app.MapRazorPages();
DeviceApi.Map(app);
app.MapGet("/healthz", () => Results.Text("ok"));

await app.RunAsync();
return 0;

public partial class Program;
