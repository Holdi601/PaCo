using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server;

/// <summary>
/// Console commands for the server owner, run inside the container:
///   docker exec -it paco dotnet PaCo.Server.dll admin-create you@example.org
///   docker exec -it paco dotnet PaCo.Server.dll admin-reset  you@example.org   (new temp password + 2FA reset)
/// There is deliberately no web sign-up: the super admin can only be created from the server's shell.
/// </summary>
public static class AdminCli
{
    public static bool IsCommand(string arg) => arg is "admin-create" or "admin-reset";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Aufruf: admin-create <email> | admin-reset <email>");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        var opts = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
        Directory.CreateDirectory(opts.DataDir);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddDbContext<PaCoDb>(o => o.UseSqlite($"Data Source={Path.Combine(opts.DataDir, "paco.db")}"));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(opts.DataDir, "dp-keys"))).SetApplicationName("PaCo");
        builder.Services.AddIdentityCore<ParentUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<PaCoDb>()
            .AddDefaultTokenProviders();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<PaCoDb>().Database.MigrateAsync();
        var users = sp.GetRequiredService<UserManager<ParentUser>>();
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync(PaCoRoles.SuperAdmin))
            await roles.CreateAsync(new IdentityRole<Guid>(PaCoRoles.SuperAdmin));

        var email = args[1].Trim();
        var password = Secrets.TemporaryPassword();
        var user = await users.FindByEmailAsync(email);

        if (args[0] == "admin-create")
        {
            if (user is not null)
            {
                Console.Error.WriteLine("Benutzer existiert bereits. Zum Zurücksetzen: admin-reset");
                return 1;
            }
            user = new ParentUser { UserName = email, Email = email, DisplayName = "Admin", MustChangePassword = true };
            Check(await users.CreateAsync(user, password));
            Check(await users.AddToRoleAsync(user, PaCoRoles.SuperAdmin));
        }
        else
        {
            if (user is null)
            {
                Console.Error.WriteLine("Benutzer nicht gefunden.");
                return 1;
            }
            Check(await users.RemovePasswordAsync(user));
            Check(await users.AddPasswordAsync(user, password));
            Check(await users.SetTwoFactorEnabledAsync(user, false));
            Check(await users.ResetAuthenticatorKeyAsync(user));
            Check(await users.SetLockoutEndDateAsync(user, null));
            user.MustChangePassword = true;
            Check(await users.UpdateAsync(user));
        }

        Console.WriteLine($"Benutzer:              {email}");
        Console.WriteLine($"Vorläufiges Passwort:  {password}");
        Console.WriteLine("Beim ersten Login müssen Passwort geändert und die Authenticator-App eingerichtet werden.");
        return 0;
    }

    private static void Check(IdentityResult r)
    {
        if (!r.Succeeded) throw new InvalidOperationException(string.Join("; ", r.Errors.Select(e => e.Description)));
    }
}
