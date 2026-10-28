using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_monitoring");

            migrationBuilder.CreateTable(
                name: "alerts",
                schema: "kernel_monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    origin = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    group_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    group_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    threshold = table.Column<int>(type: "integer", nullable: false),
                    window_minutes = table.Column<int>(type: "integer", nullable: false),
                    first_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    link = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    search = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    runbook = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    copied_revision = table.Column<int>(type: "integer", nullable: false),
                    sent_revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rule_states",
                schema: "kernel_monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failing_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counted_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rule_values",
                schema: "kernel_monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    threshold = table.Column<int>(type: "integer", nullable: true),
                    window_minutes = table.Column<int>(type: "integer", nullable: true),
                    above = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_values", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_rule_group_value_raised_at",
                schema: "kernel_monitoring",
                table: "alerts",
                columns: new[] { "rule", "group_value", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_state_raised_at",
                schema: "kernel_monitoring",
                table: "alerts",
                columns: new[] { "state", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_tenant_source_id",
                schema: "kernel_monitoring",
                table: "alerts",
                columns: new[] { "tenant", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rule_states_rule",
                schema: "kernel_monitoring",
                table: "rule_states",
                column: "rule",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rule_values_rule",
                schema: "kernel_monitoring",
                table: "rule_values",
                column: "rule",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts",
                schema: "kernel_monitoring");

            migrationBuilder.DropTable(
                name: "rule_states",
                schema: "kernel_monitoring");

            migrationBuilder.DropTable(
                name: "rule_values",
                schema: "kernel_monitoring");
        }
    }
}
