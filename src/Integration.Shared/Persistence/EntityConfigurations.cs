using Integration.Shared.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integration.Shared.Persistence;

/// <summary>
/// Column lengths, enum check constraints, foreign keys and filtered unique indexes are
/// declared here on purpose: the invariants must hold even if a handler is bypassed and a
/// row is inserted straight into SQL.
///
/// Public because the limits are part of the contract that the validators enforce BEFORE a
/// row is built: a transport identity or a trace header that cannot fit its column has to be
/// handled as a reasoned rejection, not as a SQL error inside a transaction.
/// </summary>
public static class ColumnLengths
{
    public const int Hash = 64;
    public const int ExternalReference = 80;
    public const int Currency = 3;
    public const int Kind = 16;
    public const int Status = 24;
    public const int RoutingKey = 64;
    public const int Exchange = 64;
    public const int ErrorCode = 64;
    public const int TraceParent = 64;
    public const int TraceState = 256;
    public const int ConsumerName = 64;
    public const int TransportMessageId = 128;
    public const int Fingerprint = 128;
    public const int ReasonCode = 48;
    public const int ReceiptId = 64;
    public const int Outcome = 24;
}

public sealed class ExportRecordConfiguration : IEntityTypeConfiguration<ExportRecord>
{
    public void Configure(EntityTypeBuilder<ExportRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ExportRequests", t =>
        {
            t.HasCheckConstraint("CK_ExportRequests_Amount", "[Amount] > 0");
            t.HasCheckConstraint("CK_ExportRequests_Currency", "[Currency] = 'TRY'");
            t.HasCheckConstraint("CK_ExportRequests_Reference", "LEN([ExternalReference]) BETWEEN 1 AND 80");
        });

        builder.HasKey(x => x.RequestId);
        builder.Property(x => x.RequestId).ValueGeneratedNever();
        builder.Property(x => x.PayloadHash).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Hash);
        builder.Property(x => x.ExternalReference).IsRequired().HasMaxLength(ColumnLengths.ExternalReference);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Currency).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Currency);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("OutboxMessages", t =>
        {
            t.HasCheckConstraint(
                "CK_OutboxMessages_Kind",
                "[Kind] IN ('Export', 'DeadLetter')");
            t.HasCheckConstraint(
                "CK_OutboxMessages_Status",
                "[Status] IN ('Pending', 'Publishing', 'Published')");

            // Exactly one link, and only the link the kind allows.
            t.HasCheckConstraint(
                "CK_OutboxMessages_Links",
                "([Kind] = 'Export' AND [SourceRequestId] IS NOT NULL AND [RejectionId] IS NULL) OR " +
                "([Kind] = 'DeadLetter' AND (([SourceRequestId] IS NOT NULL AND [RejectionId] IS NULL) OR " +
                "([SourceRequestId] IS NULL AND [RejectionId] IS NOT NULL)))");

            t.HasCheckConstraint(
                "CK_OutboxMessages_PublishedHasTimestamp",
                "([Status] <> 'Published') OR ([PublishedAtUtc] IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Kind).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Kind);
        builder.Property(x => x.Body).IsRequired();
        builder.Property(x => x.RoutingKey).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.RoutingKey);
        builder.Property(x => x.Exchange).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Exchange);
        builder.Property(x => x.Status).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Status);
        builder.Property(x => x.LastErrorCode).IsUnicode(false).HasMaxLength(ColumnLengths.ErrorCode);
        builder.Property(x => x.TraceParent).IsUnicode(false).HasMaxLength(ColumnLengths.TraceParent);
        builder.Property(x => x.TraceState).IsUnicode(false).HasMaxLength(ColumnLengths.TraceState);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.NextAttemptAtUtc).IsRequired();

        builder.HasOne<ExportRecord>()
            .WithMany()
            .HasForeignKey(x => x.SourceRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RejectedMessage>()
            .WithMany()
            .HasForeignKey(x => x.RejectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc, x.CreatedAtUtc })
            .HasDatabaseName("IX_OutboxMessages_Dispatch");

        // Exactly one row per (source request, kind): one Export event AND at most one
        // terminal DeadLetter event per request. A single composite filtered index holds
        // both invariants - EF keeps only one index per column set, so two separate indexes
        // on SourceRequestId alone would silently lose one of them.
        builder.HasIndex(x => new { x.SourceRequestId, x.Kind })
            .HasDatabaseName("UX_OutboxMessages_SourceRequest_Kind")
            .IsUnique()
            .HasFilter("[SourceRequestId] IS NOT NULL");

        // One DeadLetter event per quarantined message.
        builder.HasIndex(x => x.RejectionId)
            .HasDatabaseName("UX_OutboxMessages_DeadLetter_PerRejection")
            .IsUnique()
            .HasFilter("[RejectionId] IS NOT NULL");
    }
}

public sealed class IntegrationJobConfiguration : IEntityTypeConfiguration<IntegrationJob>
{
    public void Configure(EntityTypeBuilder<IntegrationJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("IntegrationJobs", t =>
        {
            t.HasCheckConstraint(
                "CK_IntegrationJobs_Status",
                "[Status] IN ('Pending', 'Processing', 'RetryScheduled', 'Completed', 'DeadLetterPending', 'DeadLettered')");
            t.HasCheckConstraint(
                "CK_IntegrationJobs_Attempts",
                "[AttemptsStarted] >= 0");
            t.HasCheckConstraint(
                "CK_IntegrationJobs_CompletedHasReceipt",
                "([Status] <> 'Completed') OR ([ExternalReceiptId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
        });

        builder.HasKey(x => x.RequestId);
        builder.Property(x => x.RequestId).ValueGeneratedNever();
        builder.Property(x => x.PayloadHash).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Hash);
        builder.Property(x => x.Status).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Status);
        builder.Property(x => x.LastErrorCode).IsUnicode(false).HasMaxLength(ColumnLengths.ErrorCode);
        builder.Property(x => x.ExternalReceiptId).IsUnicode(false).HasMaxLength(ColumnLengths.ReceiptId);
        builder.Property(x => x.TraceParent).IsUnicode(false).HasMaxLength(ColumnLengths.TraceParent);
        builder.Property(x => x.TraceState).IsUnicode(false).HasMaxLength(ColumnLengths.TraceState);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        // A job may only exist for a request this API accepted.
        builder.HasOne<ExportRecord>()
            .WithOne()
            .HasForeignKey<IntegrationJob>(x => x.RequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc })
            .HasDatabaseName("IX_IntegrationJobs_Dispatch");
    }
}

public sealed class InboxReceiptConfiguration : IEntityTypeConfiguration<InboxReceipt>
{
    public void Configure(EntityTypeBuilder<InboxReceipt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("InboxReceipts");

        builder.HasKey(x => new { x.ConsumerName, x.TransportMessageId });
        builder.Property(x => x.ConsumerName).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.ConsumerName);
        builder.Property(x => x.TransportMessageId).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.TransportMessageId);
        builder.Property(x => x.PayloadHash).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Hash);
        builder.Property(x => x.ReceivedAtUtc).IsRequired();

        builder.HasOne<IntegrationJob>()
            .WithMany()
            .HasForeignKey(x => x.IntegrationJobId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.EventId).HasDatabaseName("IX_InboxReceipts_EventId");
    }
}

public sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
{
    public void Configure(EntityTypeBuilder<JobAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("JobAttempts", t => t.HasCheckConstraint(
            "CK_JobAttempts_Outcome",
            "[Outcome] IN ('Started', 'Succeeded', 'TransientFailure', 'PermanentFailure', 'Abandoned')"));

        builder.HasKey(x => new { x.RequestId, x.AttemptNumber });
        builder.Property(x => x.Outcome).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Outcome);
        builder.Property(x => x.SafeErrorCode).IsUnicode(false).HasMaxLength(ColumnLengths.ErrorCode);
        builder.Property(x => x.StartedAtUtc).IsRequired();

        builder.HasOne<IntegrationJob>()
            .WithMany()
            .HasForeignKey(x => x.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RejectedMessageConfiguration : IEntityTypeConfiguration<RejectedMessage>
{
    public void Configure(EntityTypeBuilder<RejectedMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("RejectedMessages", t => t.HasCheckConstraint(
            "CK_RejectedMessages_BodyLength",
            "[BodyLength] >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Fingerprint).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Fingerprint);
        builder.Property(x => x.TransportMessageId).IsUnicode(false).HasMaxLength(ColumnLengths.TransportMessageId);
        builder.Property(x => x.BodySha256).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.Hash);
        builder.Property(x => x.ReasonCode).IsRequired().IsUnicode(false).HasMaxLength(ColumnLengths.ReasonCode);
        builder.Property(x => x.ReceivedAtUtc).IsRequired();

        builder.HasIndex(x => x.Fingerprint).HasDatabaseName("UX_RejectedMessages_Fingerprint").IsUnique();
    }
}
