using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Tenancy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditChains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_anchors",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_anchors", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "audit_verification_requests",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    full = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_verification_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_verifications",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    full = table.Column<bool>(type: "boolean", nullable: false),
                    from_position = table.Column<long>(type: "bigint", nullable: false),
                    to_position = table.Column<long>(type: "bigint", nullable: false),
                    to_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    events_checked = table.Column<int>(type: "integer", nullable: false),
                    problems = table.Column<string>(type: "jsonb", nullable: false),
                    restore_position = table.Column<long>(type: "bigint", nullable: true),
                    restore_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_verifications", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "unit_clients",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_clients", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_anchors_tenant_slug_kind_position",
                schema: "kernel_tenancy",
                table: "audit_anchors",
                columns: new[] { "tenant_slug", "kind", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_verification_requests_verification_id",
                schema: "kernel_tenancy",
                table: "audit_verification_requests",
                column: "verification_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_verifications_tenant_slug_created_at",
                schema: "kernel_tenancy",
                table: "audit_verifications",
                columns: new[] { "tenant_slug", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_clients_principal_id",
                schema: "kernel_tenancy",
                table: "unit_clients",
                column: "principal_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_clients_tenant_slug",
                schema: "kernel_tenancy",
                table: "unit_clients",
                column: "tenant_slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_anchors",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "audit_verification_requests",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "audit_verifications",
                schema: "kernel_tenancy");

            migrationBuilder.DropTable(
                name: "unit_clients",
                schema: "kernel_tenancy");
        }
    }
}
