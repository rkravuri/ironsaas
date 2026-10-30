using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Tenancy.Data.Migrations
{
    /// <inheritdoc />
    public partial class SuitesPerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "suite",
                schema: "kernel_tenancy",
                table: "tenant_entitlements",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "foundation");

            migrationBuilder.AddColumn<string>(
                name: "suite",
                schema: "kernel_tenancy",
                table: "plans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "foundation");

            migrationBuilder.CreateTable(
                name: "tenant_suites",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suite = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_suites", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_suites_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "kernel_tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plans_suite",
                schema: "kernel_tenancy",
                table: "plans",
                column: "suite");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_suites_tenant_id_suite",
                schema: "kernel_tenancy",
                table: "tenant_suites",
                columns: new[] { "tenant_id", "suite" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_suites",
                schema: "kernel_tenancy");

            migrationBuilder.DropIndex(
                name: "ix_plans_suite",
                schema: "kernel_tenancy",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "suite",
                schema: "kernel_tenancy",
                table: "tenant_entitlements");

            migrationBuilder.DropColumn(
                name: "suite",
                schema: "kernel_tenancy",
                table: "plans");
        }
    }
}
