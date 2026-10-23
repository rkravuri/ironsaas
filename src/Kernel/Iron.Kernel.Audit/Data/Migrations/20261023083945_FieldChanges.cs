using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class FieldChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "changes",
                schema: "kernel_audit",
                table: "events",
                type: "jsonb",
                nullable: true);

            // The guard covers the field changes as it covers every other content column (TF-060 AC2, ADR-0051).
            migrationBuilder.Sql(@"
create or replace function kernel_audit.events_guard() returns trigger
language plpgsql as $$
begin
  if old.chain_position is not null then
    raise exception 'audit event % is sealed at position %; it never changes', old.id, old.chain_position using errcode = '42501';
  end if;
  if new.chain_position is null
     or (new.id, new.created_at, new.created_by, new.event_type, new.outcome, new.actor_id, new.on_behalf_of_id,
         new.impersonator_id, new.severity, new.source, new.target, new.record_type, new.record_id, new.reason,
         new.details, new.correlation_id, new.changes)
        is distinct from
        (old.id, old.created_at, old.created_by, old.event_type, old.outcome, old.actor_id, old.on_behalf_of_id,
         old.impersonator_id, old.severity, old.source, old.target, old.record_type, old.record_id, old.reason,
         old.details, old.correlation_id, old.changes) then
    raise exception 'only the chain columns of audit event % are set, once', old.id using errcode = '42501';
  end if;
  return new;
end $$;
revoke all on function kernel_audit.events_guard() from public;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
create or replace function kernel_audit.events_guard() returns trigger
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
end $$;");

            migrationBuilder.DropColumn(
                name: "changes",
                schema: "kernel_audit",
                table: "events");
        }
    }
}
