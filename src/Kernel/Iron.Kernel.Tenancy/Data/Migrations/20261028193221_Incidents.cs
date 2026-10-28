using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Tenancy.Data.Migrations
{
    /// <inheritdoc />
    public partial class Incidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incident_entries",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_entries", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "incident_tenants",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    personal_data = table.Column<bool>(type: "boolean", nullable: false),
                    known_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notified_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notice = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    notice_escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    lead = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    communications = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    scribe = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    declared_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    review_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_recorded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    what_happened = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    why = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    what_changes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    review_escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incident_entries_incident_id_at",
                schema: "kernel_tenancy",
                table: "incident_entries",
                columns: new[] { "incident_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_incident_tenants_incident_id_tenant_slug",
                schema: "kernel_tenancy",
                table: "incident_tenants",
                columns: new[] { "incident_id", "tenant_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_status_review_due_at",
                schema: "kernel_tenancy",
                table: "incidents",
                columns: new[] { "status", "review_due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_entries",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "incident_tenants",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "kernel_tenancy");
        }
    }
}
