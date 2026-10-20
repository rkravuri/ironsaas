using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Data.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_data");

            migrationBuilder.CreateTable(
                name: "record_shares",
                schema: "kernel_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record_shares", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_record_shares_record_type_record_id_unit_id",
                schema: "kernel_data",
                table: "record_shares",
                columns: new[] { "record_type", "record_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_record_shares_unit_id",
                schema: "kernel_data",
                table: "record_shares",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "record_shares",
                schema: "kernel_data");
        }
    }
}
