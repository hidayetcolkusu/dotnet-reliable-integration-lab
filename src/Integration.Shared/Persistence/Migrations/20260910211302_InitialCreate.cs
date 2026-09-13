using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integration.Shared.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExportRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportRequests", x => x.RequestId);
                    table.CheckConstraint("CK_ExportRequests_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_ExportRequests_Currency", "[Currency] = 'TRY'");
                    table.CheckConstraint("CK_ExportRequests_Reference", "LEN([ExternalReference]) BETWEEN 1 AND 80");
                });

            migrationBuilder.CreateTable(
                name: "RejectedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    TransportMessageId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    BodySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    BodyLength = table.Column<int>(type: "int", nullable: false),
                    ReasonCode = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeadLetterPublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RejectedMessages", x => x.Id);
                    table.CheckConstraint("CK_RejectedMessages_BodyLength", "[BodyLength] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "IntegrationJobs",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    AttemptsStarted = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ExternalReceiptId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    TraceParent = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    TraceState = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationJobs", x => x.RequestId);
                    table.CheckConstraint("CK_IntegrationJobs_Attempts", "[AttemptsStarted] >= 0");
                    table.CheckConstraint("CK_IntegrationJobs_CompletedHasReceipt", "([Status] <> 'Completed') OR ([ExternalReceiptId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_IntegrationJobs_Status", "[Status] IN ('Pending', 'Processing', 'RetryScheduled', 'Completed', 'DeadLetterPending', 'DeadLettered')");
                    table.ForeignKey(
                        name: "FK_IntegrationJobs_ExportRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ExportRequests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SourceRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RejectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RoutingKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Exchange = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    PublishAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    TraceParent = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    TraceState = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_OutboxMessages_Kind", "[Kind] IN ('Export', 'DeadLetter')");
                    table.CheckConstraint("CK_OutboxMessages_Links", "([Kind] = 'Export' AND [SourceRequestId] IS NOT NULL AND [RejectionId] IS NULL) OR ([Kind] = 'DeadLetter' AND (([SourceRequestId] IS NOT NULL AND [RejectionId] IS NULL) OR ([SourceRequestId] IS NULL AND [RejectionId] IS NOT NULL)))");
                    table.CheckConstraint("CK_OutboxMessages_PublishedHasTimestamp", "([Status] <> 'Published') OR ([PublishedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_OutboxMessages_Status", "[Status] IN ('Pending', 'Publishing', 'Published')");
                    table.ForeignKey(
                        name: "FK_OutboxMessages_ExportRequests_SourceRequestId",
                        column: x => x.SourceRequestId,
                        principalTable: "ExportRequests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_RejectedMessages_RejectionId",
                        column: x => x.RejectionId,
                        principalTable: "RejectedMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboxReceipts",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    TransportMessageId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IntegrationJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxReceipts", x => new { x.ConsumerName, x.TransportMessageId });
                    table.ForeignKey(
                        name: "FK_InboxReceipts_IntegrationJobs_IntegrationJobId",
                        column: x => x.IntegrationJobId,
                        principalTable: "IntegrationJobs",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobAttempts",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    SafeErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttempts", x => new { x.RequestId, x.AttemptNumber });
                    table.CheckConstraint("CK_JobAttempts_Outcome", "[Outcome] IN ('Started', 'Succeeded', 'TransientFailure', 'PermanentFailure', 'Abandoned')");
                    table.ForeignKey(
                        name: "FK_JobAttempts_IntegrationJobs_RequestId",
                        column: x => x.RequestId,
                        principalTable: "IntegrationJobs",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxReceipts_EventId",
                table: "InboxReceipts",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxReceipts_IntegrationJobId",
                table: "InboxReceipts",
                column: "IntegrationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationJobs_Dispatch",
                table: "IntegrationJobs",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Dispatch",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessages_DeadLetter_PerRejection",
                table: "OutboxMessages",
                column: "RejectionId",
                unique: true,
                filter: "[RejectionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessages_SourceRequest_Kind",
                table: "OutboxMessages",
                columns: new[] { "SourceRequestId", "Kind" },
                unique: true,
                filter: "[SourceRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_RejectedMessages_Fingerprint",
                table: "RejectedMessages",
                column: "Fingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxReceipts");

            migrationBuilder.DropTable(
                name: "JobAttempts");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "IntegrationJobs");

            migrationBuilder.DropTable(
                name: "RejectedMessages");

            migrationBuilder.DropTable(
                name: "ExportRequests");
        }
    }
}
