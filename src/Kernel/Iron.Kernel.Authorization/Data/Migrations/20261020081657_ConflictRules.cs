using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConflictRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conflict_report_entries",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    first_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_delegation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_role = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    first_scope_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    first_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_unit_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    first_source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    second_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    second_delegation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    second_role = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    second_scope_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    second_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    second_unit_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    second_source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unit_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflict_report_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "conflict_report_runs",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rules = table.Column<int>(type: "integer", nullable: false),
                    conflicts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflict_report_runs", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "conflict_rules",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    first_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    first_value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    second_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    second_value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflict_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conflict_report_entries_first_assignment_id",
                schema: "kernel_authz",
                table: "conflict_report_entries",
                column: "first_assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_report_entries_principal_id",
                schema: "kernel_authz",
                table: "conflict_report_entries",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_report_entries_rule_id",
                schema: "kernel_authz",
                table: "conflict_report_entries",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_report_entries_second_assignment_id",
                schema: "kernel_authz",
                table: "conflict_report_entries",
                column: "second_assignment_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conflict_report_entries",
                schema: "kernel_authz");

            migrationBuilder.DropTable(
                name: "conflict_report_runs",
                schema: "kernel_authz");

            migrationBuilder.DropTable(
                name: "conflict_rules",
                schema: "kernel_authz");
        }
    }
}
