using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Fleet.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryResetFactors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "reset_factors",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reset_factors",
                schema: "kernel_fleet",
                table: "provisioning_jobs");
        }
    }
}
