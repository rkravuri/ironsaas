using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Events.Data.Migrations
{
    /// <inheritdoc />
    public partial class HeldDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "held_at",
                schema: "kernel_events",
                table: "inbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suite",
                schema: "kernel_events",
                table: "inbox",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "held_at",
                schema: "kernel_events",
                table: "inbox");

            migrationBuilder.DropColumn(
                name: "suite",
                schema: "kernel_events",
                table: "inbox");
        }
    }
}
