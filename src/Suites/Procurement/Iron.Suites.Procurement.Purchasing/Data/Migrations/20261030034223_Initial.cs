using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Suites.Procurement.Purchasing.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "procurement_purchasing");

            migrationBuilder.CreateTable(
                name: "installations",
                schema: "procurement_purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    suite = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    installed_by_job = table.Column<Guid>(type: "uuid", nullable: true),
                    installed_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seeded_by_job = table.Column<Guid>(type: "uuid", nullable: true),
                    seeded_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_installations_suite",
                schema: "procurement_purchasing",
                table: "installations",
                column: "suite",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installations",
                schema: "procurement_purchasing");
        }
    }
}
