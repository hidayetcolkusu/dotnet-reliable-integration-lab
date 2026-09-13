using Integration.Shared.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Integration.Shared.Persistence;

/// <summary>
/// The application database (IntegrationLab). It never reaches into the external system's
/// database: there is no FK, no join and no shared transaction with FakeErpLab.
/// </summary>
public sealed class LabDbContext : DbContext
{
    public LabDbContext(DbContextOptions<LabDbContext> options)
        : base(options)
    {
    }

    public DbSet<ExportRecord> ExportRequests => Set<ExportRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();

    public DbSet<IntegrationJob> IntegrationJobs => Set<IntegrationJob>();

    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();

    public DbSet<RejectedMessage> RejectedMessages => Set<RejectedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LabDbContext).Assembly);
    }
}
