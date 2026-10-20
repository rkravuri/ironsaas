using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionWorkingUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "all_units",
                schema: "kernel_identity",
                table: "sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "working_unit_id",
                schema: "kernel_identity",
                table: "sessions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "all_units",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "working_unit_id",
                schema: "kernel_identity",
                table: "sessions");
        }
    }
}
