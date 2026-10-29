using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Samples.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_samples");

            migrationBuilder.CreateTable(
                name: "records",
                schema: "kernel_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    owning_organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sharing_mode = table.Column<int>(type: "integer", nullable: false),
                    visibility_tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_records_name",
                schema: "kernel_samples",
                table: "records",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_records_owning_organisation_id",
                schema: "kernel_samples",
                table: "records",
                column: "owning_organisation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "records",
                schema: "kernel_samples");
        }
    }
}
