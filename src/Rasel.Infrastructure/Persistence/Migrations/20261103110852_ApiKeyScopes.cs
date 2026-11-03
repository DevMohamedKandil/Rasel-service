using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rasel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAt",
                table: "ApiClients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ExpiresAt",
                table: "ApiClients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastUsedAt",
                table: "ApiClients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "ApiClients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1); // every key that exists before scopes were introduced could already do everything: Admin (new keys state a scope explicitly)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ApiClients");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "ApiClients");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "ApiClients");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "ApiClients");
        }
    }
}
