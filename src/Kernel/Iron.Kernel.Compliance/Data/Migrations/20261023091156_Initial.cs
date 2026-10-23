using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Compliance.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_compliance");

            migrationBuilder.CreateTable(
                name: "audit_trail_reviews",
                schema: "kernel_compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    unit_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    event_types = table.Column<List<string>>(type: "text[]", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    searches = table.Column<string>(type: "jsonb", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_trail_reviews", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "signatures",
                schema: "kernel_compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    snapshot_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    snapshot_form = table.Column<short>(type: "smallint", nullable: false),
                    signer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    signer_login_id = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    method = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    meaning = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signatures", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_audit_trail_reviews_period_from_period_to",
                schema: "kernel_compliance",
                table: "audit_trail_reviews",
                columns: new[] { "period_from", "period_to" });

            migrationBuilder.CreateIndex(
                name: "ix_signatures_record_type_record_id_created_at",
                schema: "kernel_compliance",
                table: "signatures",
                columns: new[] { "record_type", "record_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_signatures_signer_id",
                schema: "kernel_compliance",
                table: "signatures",
                column: "signer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_trail_reviews",
                schema: "kernel_compliance");

            migrationBuilder.DropTable(
                name: "signatures",
                schema: "kernel_compliance");
        }
    }
}
