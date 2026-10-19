using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class HomeUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "home_unit_id",
                schema: "kernel_identity",
                table: "principals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_principals_home_unit_id",
                schema: "kernel_identity",
                table: "principals",
                column: "home_unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_principals_home_unit_id",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "home_unit_id",
                schema: "kernel_identity",
                table: "principals");
        }
    }
}
