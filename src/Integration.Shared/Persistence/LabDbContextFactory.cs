using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Integration.Shared.Persistence;

/// <summary>
/// Design-time factory for `dotnet-ef` (migrations add / script). It never connects to a
/// database when scaffolding; the connection string only has to be well-formed.
/// </summary>
public sealed class LabDbContextFactory : IDesignTimeDbContextFactory<LabDbContext>
{
    public LabDbContext CreateDbContext(string[] args)
    {
        const string placeholder =
            "Server=127.0.0.1,11433;Database=IntegrationLab;User Id=sa;Password=design-time-only;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(placeholder, sql => sql.MigrationsAssembly(typeof(LabDbContext).Assembly.FullName))
            .Options;

        return new LabDbContext(options);
    }
}
