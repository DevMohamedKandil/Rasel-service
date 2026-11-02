using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rasel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DailyMessageLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetType = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Destination = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Body = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LeasedUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OtpChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PhoneE164 = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ResendAvailableAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_ApiKeyHash",
                table: "ApiClients",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ClientId_CreatedAt",
                table: "Messages",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ClientId_IdempotencyKey",
                table: "Messages",
                columns: new[] { "ClientId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Status_NextAttemptAt",
                table: "Messages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_ClientId_PhoneE164_Purpose",
                table: "OtpChallenges",
                columns: new[] { "ClientId", "PhoneE164", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "UX_OtpChallenges_Active",
                table: "OtpChallenges",
                columns: new[] { "ClientId", "PhoneE164", "Purpose" },
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiClients");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "OtpChallenges");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
