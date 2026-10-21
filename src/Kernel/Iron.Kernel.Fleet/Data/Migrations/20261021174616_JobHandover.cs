using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Fleet.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobHandover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handed_over_at",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_by_id",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_jobs_requested_by_id",
                schema: "kernel_fleet",
                table: "provisioning_jobs",
                column: "requested_by_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provisioning_jobs_requested_by_id",
                schema: "kernel_fleet",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "handed_over_at",
                schema: "kernel_fleet",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "requested_by_id",
                schema: "kernel_fleet",
                table: "provisioning_jobs");
        }
    }
}
