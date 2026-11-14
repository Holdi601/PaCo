using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PaCo.Server.Services;

namespace PaCo.Server.Data;

public sealed class PaCoDb(DbContextOptions<PaCoDb> options, IFamilyContext? family = null)
    : IdentityDbContext<ParentUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>
    /// Family of the signed-in parent. <see cref="Guid.Empty"/> when there is none (super admin, device API,
    /// anonymous) – then every family-scoped query returns nothing unless the code explicitly opts out with
    /// <c>IgnoreQueryFilters()</c>. Deny by default.
    /// </summary>
    public Guid CurrentFamilyId => family?.FamilyId ?? Guid.Empty;

    public DbSet<Family> Families => Set<Family>();

    public DbSet<Child> Children => Set<Child>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceAccount> DeviceAccounts => Set<DeviceAccount>();

    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();

    public DbSet<TimeGrantEntity> TimeGrants => Set<TimeGrantEntity>();

    public DbSet<UnlockCodeIssue> UnlockCodes => Set<UnlockCodeIssue>();

    public DbSet<UsageDay> UsageDays => Set<UsageDay>();

    public DbSet<DeviceEventEntity> DeviceEvents => Set<DeviceEventEntity>();

    public DbSet<AlertEntity> Alerts => Set<AlertEntity>();

    public DbSet<ScreenshotEntity> Screenshots => Set<ScreenshotEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite cannot compare/order DateTimeOffset; store as sortable binary (all values are UTC).
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ParentUser>().HasOne(u => u.Family).WithMany().HasForeignKey(u => u.FamilyId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Child>().HasOne<Family>().WithMany().HasForeignKey(c => c.FamilyId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Device>().HasOne<Family>().WithMany().HasForeignKey(d => d.FamilyId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Device>().HasIndex(d => d.TokenHash).IsUnique();

        b.Entity<DeviceAccount>().HasOne(a => a.Device).WithMany(d => d.Accounts).HasForeignKey(a => a.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeviceAccount>().HasOne(a => a.Child).WithMany().HasForeignKey(a => a.ChildId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<DeviceAccount>().HasIndex(a => new { a.DeviceId, a.Sid }).IsUnique();

        b.Entity<PairingCode>().HasIndex(p => p.CodeHash).IsUnique();
        b.Entity<PairingCode>().HasOne<Family>().WithMany().HasForeignKey(p => p.FamilyId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<TimeGrantEntity>().HasOne<Child>().WithMany().HasForeignKey(g => g.ChildId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TimeGrantEntity>().HasIndex(g => new { g.ChildId, g.Day });

        b.Entity<UnlockCodeIssue>().HasOne<Child>().WithMany().HasForeignKey(c => c.ChildId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UnlockCodeIssue>().HasIndex(c => new { c.ChildId, c.Day, c.Nonce }).IsUnique();

        b.Entity<UsageDay>().HasOne<Child>().WithMany().HasForeignKey(u => u.ChildId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UsageDay>().HasOne<Device>().WithMany().HasForeignKey(u => u.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UsageDay>().HasIndex(u => new { u.DeviceId, u.ChildId, u.Day }).IsUnique();
        b.Entity<UsageDay>().HasIndex(u => new { u.ChildId, u.Day });

        b.Entity<DeviceEventEntity>().HasOne<Device>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeviceEventEntity>().HasIndex(e => new { e.FamilyId, e.TimeUtc });

        b.Entity<AlertEntity>().HasOne<Child>().WithMany().HasForeignKey(a => a.ChildId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AlertEntity>().HasIndex(a => new { a.FamilyId, a.TimeUtc });
        b.Entity<ScreenshotEntity>().HasOne<Child>().WithMany().HasForeignKey(s => s.ChildId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ScreenshotEntity>().HasIndex(s => new { s.ChildId, s.RequestedUtc });

        // Tenant isolation for every family-scoped entity.
        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(IFamilyScoped).IsAssignableFrom(t.ClrType)))
        {
            var method = typeof(PaCoDb).GetMethod(nameof(FamilyFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(type.ClrType);
            b.Entity(type.ClrType).HasQueryFilter((LambdaExpression)method.Invoke(this, null)!);
        }
    }

    private Expression<Func<T, bool>> FamilyFilter<T>() where T : class, IFamilyScoped =>
        e => e.FamilyId == CurrentFamilyId;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardFamilyWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardFamilyWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Defence in depth: when a parent is signed in, writes to rows of another family are refused even if some
    /// page forgot a check. Device API / admin code run without a family context and set FamilyId explicitly.
    /// </summary>
    private void GuardFamilyWrites()
    {
        if (CurrentFamilyId == Guid.Empty) return;
        foreach (var entry in ChangeTracker.Entries<IFamilyScoped>())
        {
            if ((entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) && entry.Entity.FamilyId != CurrentFamilyId)
                throw new InvalidOperationException("Zugriff auf Daten einer anderen Familie verweigert.");
        }
    }
}
