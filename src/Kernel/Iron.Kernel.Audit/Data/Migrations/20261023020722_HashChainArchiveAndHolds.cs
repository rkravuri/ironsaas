using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class HashChainArchiveAndHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "chain_position",
                schema: "kernel_audit",
                table: "events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hash",
                schema: "kernel_audit",
                table: "events",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "hash_version",
                schema: "kernel_audit",
                table: "events",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_hash",
                schema: "kernel_audit",
                table: "events",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sealed_at",
                schema: "kernel_audit",
                table: "events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "archive_batches",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_position = table.Column<long>(type: "bigint", nullable: false),
                    last_position = table.Column<long>(type: "bigint", nullable: false),
                    first_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    blob_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    last_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_batches", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "exports",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    blob_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rows = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exports", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "legal_holds",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    record_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_holds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "prunes",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    up_to_position = table.Column<long>(type: "bigint", nullable: false),
                    hash_at_position = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    batches_removed = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prunes", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "retention_choices",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_choices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seals",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_position = table.Column<long>(type: "bigint", nullable: false),
                    last_position = table.Column<long>(type: "bigint", nullable: false),
                    last_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seals", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_events_chain_position",
                schema: "kernel_audit",
                table: "events",
                column: "chain_position",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_sealed_at",
                schema: "kernel_audit",
                table: "events",
                column: "sealed_at");

            migrationBuilder.CreateIndex(
                name: "ix_archive_batches_first_at_last_at",
                schema: "kernel_audit",
                table: "archive_batches",
                columns: new[] { "first_at", "last_at" });

            migrationBuilder.CreateIndex(
                name: "ix_archive_batches_last_position",
                schema: "kernel_audit",
                table: "archive_batches",
                column: "last_position",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_record_type_record_id",
                schema: "kernel_audit",
                table: "legal_holds",
                columns: new[] { "record_type", "record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_prunes_up_to_position",
                schema: "kernel_audit",
                table: "prunes",
                column: "up_to_position",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retention_choices_class_code",
                schema: "kernel_audit",
                table: "retention_choices",
                column: "class_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seals_last_position",
                schema: "kernel_audit",
                table: "seals",
                column: "last_position",
                unique: true);

            // The chain's guards (TF-060 AC2, TF-061, ADR-0050). A sealed event never changes, and an unsealed one may
            // only take its chain columns, once: for any role, the owner's included, while the trigger stands.
            migrationBuilder.Sql(@"
create function kernel_audit.events_guard() returns trigger
language plpgsql as $$
begin
  if old.chain_position is not null then
    raise exception 'audit event % is sealed at position %; it never changes', old.id, old.chain_position using errcode = '42501';
  end if;
  if new.chain_position is null
     or (new.id, new.created_at, new.created_by, new.event_type, new.outcome, new.actor_id, new.on_behalf_of_id,
         new.impersonator_id, new.severity, new.source, new.target, new.record_type, new.record_id, new.reason,
         new.details, new.correlation_id)
        is distinct from
        (old.id, old.created_at, old.created_by, old.event_type, old.outcome, old.actor_id, old.on_behalf_of_id,
         old.impersonator_id, old.severity, old.source, old.target, old.record_type, old.record_id, old.reason,
         old.details, old.correlation_id) then
    raise exception 'only the chain columns of audit event % are set, once', old.id using errcode = '42501';
  end if;
  return new;
end $$;
revoke all on function kernel_audit.events_guard() from public;
create trigger events_guard before update on kernel_audit.events for each row execute function kernel_audit.events_guard();");

            // The one way to seal: the app role has no UPDATE on the events, and runs this function, which the owner
            // owns, to set an event's chain columns once, at the position right after the chain's head.
            migrationBuilder.Sql(@"
create function kernel_audit.seal_event(p_id uuid, p_position bigint, p_previous_hash character(64), p_hash character(64), p_version smallint, p_sealed_at timestamptz)
returns void language plpgsql security definer set search_path = pg_catalog, pg_temp as $$
declare
  head bigint;
begin
  select greatest(coalesce((select max(chain_position) from kernel_audit.events), 0),
                  coalesce((select max(last_position) from kernel_audit.seals), 0))
    into head;
  if p_position <> head + 1 then
    raise exception 'audit position % does not follow the chain''s head %', p_position, head using errcode = '23514';
  end if;
  update kernel_audit.events
     set chain_position = p_position, previous_hash = p_previous_hash, hash = p_hash, hash_version = p_version, sealed_at = p_sealed_at
   where id = p_id and chain_position is null;
  if not found then
    raise exception 'audit event % is unknown or sealed already', p_id using errcode = '23514';
  end if;
end $$;
revoke all on function kernel_audit.seal_event(uuid, bigint, character, character, smallint, timestamptz) from public;");

            // The one way an audit row leaves the database (TF-064 AC2, ADR-0050): from the oldest end, once an archive
            // batch in the index holds it. The app role has no DELETE on the events.
            migrationBuilder.Sql(@"
create function kernel_audit.remove_archived(p_up_to bigint)
returns integer language plpgsql security definer set search_path = pg_catalog, pg_temp as $$
declare
  removed integer;
begin
  delete from kernel_audit.events e
   where e.chain_position is not null
     and e.chain_position <= p_up_to
     and exists (select 1 from kernel_audit.archive_batches b where e.chain_position between b.first_position and b.last_position);
  get diagnostics removed = row_count;
  return removed;
end $$;
revoke all on function kernel_audit.remove_archived(bigint) from public;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop function kernel_audit.remove_archived(bigint);");
            migrationBuilder.Sql("drop function kernel_audit.seal_event(uuid, bigint, character, character, smallint, timestamptz);");
            migrationBuilder.Sql("drop trigger events_guard on kernel_audit.events; drop function kernel_audit.events_guard();");

            migrationBuilder.DropTable(
                name: "archive_batches",
                schema: "kernel_audit");

            migrationBuilder.DropTable(
                name: "exports",
                schema: "kernel_audit");

            migrationBuilder.DropTable(
                name: "legal_holds",
                schema: "kernel_audit");

            migrationBuilder.DropTable(
                name: "prunes",
                schema: "kernel_audit");

            migrationBuilder.DropTable(
                name: "retention_choices",
                schema: "kernel_audit");

            migrationBuilder.DropTable(
                name: "seals",
                schema: "kernel_audit");

            migrationBuilder.DropIndex(
                name: "ix_events_chain_position",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_sealed_at",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "chain_position",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "hash",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "hash_version",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "previous_hash",
                schema: "kernel_audit",
                table: "events");

            migrationBuilder.DropColumn(
                name: "sealed_at",
                schema: "kernel_audit",
                table: "events");
        }
    }
}
