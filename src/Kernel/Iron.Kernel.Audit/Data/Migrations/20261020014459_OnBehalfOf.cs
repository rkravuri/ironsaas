using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class OnBehalfOf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "on_behalf_of_id",
                schema: "kernel_audit",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_on_behalf_of_id",
                schema: "kernel_audit",
                table: "events",
                column: "on_behalf_of_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_on_behalf_of_id",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "on_behalf_of_id",
                schema: "kernel_audit",
                table: "events");
        }
    }
}
