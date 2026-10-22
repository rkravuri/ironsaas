using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImpersonatorAndSeverity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "severity",
                schema: "kernel_audit",
                table: "events",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.CreateIndex(
                name: "ix_events_impersonator_id",
                schema: "kernel_audit",
                table: "events",
                column: "impersonator_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_impersonator_id",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "severity",
                schema: "kernel_audit",
                table: "events");
        }
    }
}
