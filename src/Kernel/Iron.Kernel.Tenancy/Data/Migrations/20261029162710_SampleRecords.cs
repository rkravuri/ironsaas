using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Tenancy.Data.Migrations
{
    /// <inheritdoc />
    public partial class SampleRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sample_records",
                schema: "kernel_tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sample_records",
                schema: "kernel_tenancy",
                table: "tenants");
        }
    }
}
