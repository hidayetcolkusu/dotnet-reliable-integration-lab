using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FakeErp;

/// <summary>
/// The external system's durable idempotency record: one row per operation key, forever.
/// This table is what keeps the external effect single when a
/// response is lost - the replay returns the same receipt instead of applying again.
/// </summary>
public sealed class AppliedExport
{
    public string OperationKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public string ExternalReceiptId { get; set; } = string.Empty;

    public string ExternalReference { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTimeOffset AppliedAtUtc { get; set; }
}

/// <summary>
/// FakeErpLab lives in its own database with its own schema. There is deliberately NO
/// foreign key, join or shared transaction with IntegrationLab: SQL transactions cannot
/// make an external-system boundary atomic, and this lab refuses to pretend otherwise.
/// </summary>
public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options) : DbContext(options)
{
    public DbSet<AppliedExport> AppliedExports => Set<AppliedExport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AppliedExport>(entity =>
        {
            entity.ToTable("AppliedExports", t =>
            {
                t.HasCheckConstraint("CK_AppliedExports_Amount", "[Amount] > 0");
                t.HasCheckConstraint("CK_AppliedExports_Currency", "[Currency] = 'TRY'");
                t.HasCheckConstraint(
                    "CK_AppliedExports_Reference",
                    "LEN([ExternalReference]) BETWEEN 1 AND 80");
            });

            entity.HasKey(x => x.OperationKey);
            entity.Property(x => x.OperationKey).IsRequired().IsUnicode(false).HasMaxLength(64);
            entity.Property(x => x.PayloadHash).IsRequired().IsUnicode(false).HasMaxLength(64);
            entity.Property(x => x.ExternalReceiptId).IsRequired().IsUnicode(false).HasMaxLength(64);
            entity.Property(x => x.ExternalReference).IsRequired().HasMaxLength(80);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).IsRequired().IsUnicode(false).HasMaxLength(3);
            entity.Property(x => x.AppliedAtUtc).IsRequired();

            entity.HasIndex(x => x.ExternalReceiptId).IsUnique().HasDatabaseName("UX_AppliedExports_Receipt");
            entity.HasIndex(x => x.ExternalReference).HasDatabaseName("IX_AppliedExports_Reference");
        });
    }
}

/// <summary>Design-time factory for `dotnet-ef`; it never needs a live database.</summary>
public sealed class ErpDbContextFactory : IDesignTimeDbContextFactory<ErpDbContext>
{
    public ErpDbContext CreateDbContext(string[] args)
    {
        const string placeholder =
            "Server=127.0.0.1,11433;Database=FakeErpLab;User Id=sa;Password=design-time-only;TrustServerCertificate=True";

        return new ErpDbContext(
            new DbContextOptionsBuilder<ErpDbContext>()
                .UseSqlServer(placeholder, sql => sql.MigrationsAssembly(typeof(ErpDbContext).Assembly.FullName))
                .Options);
    }
}
