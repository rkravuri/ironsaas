using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Entitlements.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_entitlements");

            migrationBuilder.CreateTable(
                name: "last_known",
                schema: "kernel_entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_last_known", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seats",
                schema: "kernel_entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_last_known_key",
                schema: "kernel_entitlements",
                table: "last_known",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seats_module_code_principal_id",
                schema: "kernel_entitlements",
                table: "seats",
                columns: new[] { "module_code", "principal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seats_principal_id",
                schema: "kernel_entitlements",
                table: "seats",
                column: "principal_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "last_known",
                schema: "kernel_entitlements");

            migrationBuilder.DropTable(
                name: "seats",
                schema: "kernel_entitlements");
        }
    }
}
