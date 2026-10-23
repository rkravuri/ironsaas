using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Tenancy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditChainSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_anchors_tenant_slug_kind_position",
                schema: "kernel_tenancy",
                table: "audit_anchors");

            migrationBuilder.AddColumn<int>(
                name: "segment",
                schema: "kernel_tenancy",
                table: "audit_anchors",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "audit_restores",
                schema: "kernel_tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    segment = table.Column<int>(type: "integer", nullable: false),
                    start_position = table.Column<long>(type: "bigint", nullable: false),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    restore_position = table.Column<long>(type: "bigint", nullable: false),
                    restore_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lost_from = table.Column<long>(type: "bigint", nullable: false),
                    lost_to = table.Column<long>(type: "bigint", nullable: false),
                    ticket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    accepted_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_restores", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_audit_anchors_tenant_slug_segment_kind_position",
                schema: "kernel_tenancy",
                table: "audit_anchors",
                columns: new[] { "tenant_slug", "segment", "kind", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_restores_tenant_slug_segment",
                schema: "kernel_tenancy",
                table: "audit_restores",
                columns: new[] { "tenant_slug", "segment" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_restores",
                schema: "kernel_tenancy");

            migrationBuilder.DropIndex(
                name: "ix_audit_anchors_tenant_slug_segment_kind_position",
                schema: "kernel_tenancy",
                table: "audit_anchors");

            migrationBuilder.DropColumn(
                name: "segment",
                schema: "kernel_tenancy",
                table: "audit_anchors");

            migrationBuilder.CreateIndex(
                name: "ix_audit_anchors_tenant_slug_kind_position",
                schema: "kernel_tenancy",
                table: "audit_anchors",
                columns: new[] { "tenant_slug", "kind", "position" },
                unique: true);
        }
    }
}
