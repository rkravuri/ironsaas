using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Fleet.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason",
                schema: "kernel_fleet",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "requested_by",
                schema: "kernel_fleet",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "subject",
                schema: "kernel_fleet",
                table: "provisioning_jobs");
        }
    }
}
