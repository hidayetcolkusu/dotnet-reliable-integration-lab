using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FakeErp.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppliedExports",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExternalReceiptId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppliedExports", x => x.OperationKey);
                    table.CheckConstraint("CK_AppliedExports_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_AppliedExports_Currency", "[Currency] = 'TRY'");
                    table.CheckConstraint("CK_AppliedExports_Reference", "LEN([ExternalReference]) BETWEEN 1 AND 80");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppliedExports_Reference",
                table: "AppliedExports",
                column: "ExternalReference");

            migrationBuilder.CreateIndex(
                name: "UX_AppliedExports_Receipt",
                table: "AppliedExports",
                column: "ExternalReceiptId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppliedExports");
        }
    }
}
