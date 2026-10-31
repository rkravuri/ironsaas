using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Iron.Kernel.Events.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_events");

            migrationBuilder.CreateTable(
                name: "archives",
                schema: "kernel_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_position = table.Column<long>(type: "bigint", nullable: false),
                    to_position = table.Column<long>(type: "bigint", nullable: false),
                    from_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    to_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    object_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archives", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "kernel_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false),
                    record_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replays = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "kernel_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    record_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_behalf_of_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    fanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archives_to_time",
                schema: "kernel_events",
                table: "archives",
                column: "to_time");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_due",
                schema: "kernel_events",
                table: "inbox",
                columns: new[] { "next_attempt_at", "position" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_event_id",
                schema: "kernel_events",
                table: "inbox",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_status_dead_at",
                schema: "kernel_events",
                table: "inbox",
                columns: new[] { "status", "dead_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_subscriber_event_id",
                schema: "kernel_events",
                table: "inbox",
                columns: new[] { "subscriber", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_waiting",
                schema: "kernel_events",
                table: "inbox",
                columns: new[] { "subscriber", "record_type", "record_id", "position" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_correlation_id",
                schema: "kernel_events",
                table: "outbox",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_occurred_at",
                schema: "kernel_events",
                table: "outbox",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "kernel_events",
                table: "outbox",
                column: "position",
                filter: "fanned_at is null");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_position",
                schema: "kernel_events",
                table: "outbox",
                column: "position",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_record_type_record_id_position",
                schema: "kernel_events",
                table: "outbox",
                columns: new[] { "record_type", "record_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_type_occurred_at",
                schema: "kernel_events",
                table: "outbox",
                columns: new[] { "type", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archives",
                schema: "kernel_events");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "kernel_events");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "kernel_events");
        }
    }
}
