using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecretsThroughTheKeyService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_secret",
                schema: "kernel_identity",
                table: "authentication_challenges");

            migrationBuilder.AddColumn<string>(
                name: "secret",
                schema: "kernel_identity",
                table: "factors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "held_secret",
                schema: "kernel_identity",
                table: "authentication_challenges",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "secret",
                schema: "kernel_identity",
                table: "factors");

            migrationBuilder.DropColumn(
                name: "held_secret",
                schema: "kernel_identity",
                table: "authentication_challenges");

            migrationBuilder.AddColumn<byte[]>(
                name: "pending_secret",
                schema: "kernel_identity",
                table: "authentication_challenges",
                type: "bytea",
                nullable: true);
        }
    }
}
