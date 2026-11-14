using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PaCo.Server.Data;

/// <summary>Used only by "dotnet ef migrations add".</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaCoDb>
{
    public PaCoDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaCoDb>().UseSqlite("Data Source=design-time.db").Options);
}
