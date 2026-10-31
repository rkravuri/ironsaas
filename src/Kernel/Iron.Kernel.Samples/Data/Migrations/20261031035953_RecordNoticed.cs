using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Samples.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordNoticed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "noticed_at",
                schema: "kernel_samples",
                table: "records",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "noticed_at",
                schema: "kernel_samples",
                table: "records");
        }
    }
}
