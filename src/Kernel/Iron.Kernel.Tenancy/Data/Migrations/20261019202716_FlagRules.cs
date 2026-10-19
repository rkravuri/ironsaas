using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Tenancy.Data.Migrations
{
    /// <inheritdoc />
    public partial class FlagRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flag_publications",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    published_revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flag_publications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "flag_rule_history",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    flag_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: false),
                    after = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flag_rule_history", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "flag_rules",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    flag_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    region = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    condition_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percentage = table.Column<int>(type: "integer", nullable: true),
                    rollout_of = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    attribute = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    attribute_values = table.Column<List<string>>(type: "text[]", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flag_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_flag_publications_path",
                schema: "kernel_tenancy",
                table: "flag_publications",
                column: "path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_flag_rule_history_flag_key_revision",
                schema: "kernel_tenancy",
                table: "flag_rule_history",
                columns: new[] { "flag_key", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_flag_rules_flag_key_position",
                schema: "kernel_tenancy",
                table: "flag_rules",
                columns: new[] { "flag_key", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_flag_rules_tenant_slug",
                schema: "kernel_tenancy",
                table: "flag_rules",
                column: "tenant_slug");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flag_publications",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "flag_rule_history",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "flag_rules",
                schema: "kernel_tenancy");
        }
    }
}
