using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rasel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecipientKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipientKey",
                table: "Messages",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ClientId_RecipientKey_CreatedAt",
                table: "Messages",
                columns: new[] { "ClientId", "RecipientKey", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ClientId_RecipientKey_CreatedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "RecipientKey",
                table: "Messages");
        }
    }
}
