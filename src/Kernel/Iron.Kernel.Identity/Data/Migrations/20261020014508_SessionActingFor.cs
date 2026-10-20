using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionActingFor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "acting_for_id",
                schema: "kernel_identity",
                table: "sessions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acting_for_id",
                schema: "kernel_identity",
                table: "sessions");
        }
    }
}
