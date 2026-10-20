using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnitMergedInto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_id",
                schema: "kernel_authz",
                table: "organisation_units",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "merged_into_id",
                schema: "kernel_authz",
                table: "organisation_units");
        }
    }
}
