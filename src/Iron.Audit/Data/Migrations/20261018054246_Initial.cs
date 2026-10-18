using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    impersonator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    target = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    record_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_events_actor_id",
                schema: "audit",
                table: "events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_correlation_id",
                schema: "audit",
                table: "events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_created_at",
                schema: "audit",
                table: "events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_events_event_type",
                schema: "audit",
                table: "events",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "ix_events_record_type_record_id",
                schema: "audit",
                table: "events",
                columns: new[] { "record_type", "record_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "audit");
        }
    }
}
