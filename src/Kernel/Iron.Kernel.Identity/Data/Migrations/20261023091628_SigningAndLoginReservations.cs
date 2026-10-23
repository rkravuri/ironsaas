using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SigningAndLoginReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_confirmed_at",
                schema: "kernel_identity",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "signing_since",
                schema: "kernel_identity",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "login_id_reservations",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_id = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_id_reservations", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_login_id_reservations_login_id",
                schema: "kernel_identity",
                table: "login_id_reservations",
                column: "login_id",
                unique: true);

            // Every principal's identifier is reserved by the database itself, so no way of creating a person can miss it,
            // and the reservation outlives the principal: an identifier is never given to a second person (TF-219,
            // ADR-0051). Identifiers never change; the trigger refuses a change rather than leave the new one unreserved.
            migrationBuilder.Sql(@"
insert into kernel_identity.login_id_reservations (id, login_id, principal_id, created_at, created_by)
select id, login_id, id, created_at, created_by from kernel_identity.principals
on conflict do nothing;

create or replace function kernel_identity.reserve_login_id() returns trigger
language plpgsql as $$
begin
  if tg_op = 'UPDATE' then
    raise exception 'the login identifier of principal % never changes', old.id using errcode = '42501';
  end if;
  if exists (select 1 from kernel_identity.login_id_reservations r where r.login_id = new.login_id and r.principal_id <> new.id) then
    raise exception 'login identifier % belonged to someone before; it is never given to a second person', new.login_id using errcode = '23505';
  end if;
  insert into kernel_identity.login_id_reservations (id, login_id, principal_id, created_at, created_by)
  values (new.id, new.login_id, new.id, new.created_at, new.created_by)
  on conflict (id) do nothing;
  return new;
end $$;
revoke all on function kernel_identity.reserve_login_id() from public;

create trigger principals_reserve_login_id
  before insert or update of login_id on kernel_identity.principals
  for each row execute function kernel_identity.reserve_login_id();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
drop trigger if exists principals_reserve_login_id on kernel_identity.principals;
drop function if exists kernel_identity.reserve_login_id();");

            migrationBuilder.DropTable(
                name: "login_id_reservations",
                schema: "kernel_identity");

            migrationBuilder.DropColumn(
                name: "provider_confirmed_at",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "signing_since",
                schema: "kernel_identity",
                table: "sessions");
        }
    }
}
