using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class PasswordPolicyAndSecurityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_changed_at",
                schema: "kernel_identity",
                table: "principals",
                type: "timestamp with time zone",
                nullable: true);

            // A password set before T2 has no date: its maximum age counts from this migration, so a tenant that turns
            // the age on does not expire everyone at once, and nobody escapes it for ever (ADR-0046).
            migrationBuilder.Sql("update kernel_identity.principals set password_changed_at = now() where password_hash is not null;");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recovery_expires_at",
                schema: "kernel_identity",
                table: "principals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recovery_token_hash",
                schema: "kernel_identity",
                table: "principals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "login_throttles",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_id = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    window_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_throttles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "password_history",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_history_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_events_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_principals_recovery_token_hash",
                schema: "kernel_identity",
                table: "principals",
                column: "recovery_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_throttles_locked_until",
                schema: "kernel_identity",
                table: "login_throttles",
                column: "locked_until");

            migrationBuilder.CreateIndex(
                name: "ix_login_throttles_login_id",
                schema: "kernel_identity",
                table: "login_throttles",
                column: "login_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_history_principal_id_set_at",
                schema: "kernel_identity",
                table: "password_history",
                columns: new[] { "principal_id", "set_at" });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_principal_id_occurred_at",
                schema: "kernel_identity",
                table: "security_events",
                columns: new[] { "principal_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_throttles",
                schema: "kernel_identity");

            migrationBuilder.DropTable(
                name: "password_history",
                schema: "kernel_identity");

            migrationBuilder.DropTable(
                name: "security_events",
                schema: "kernel_identity");

            migrationBuilder.DropIndex(
                name: "ix_principals_recovery_token_hash",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "password_changed_at",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "recovery_expires_at",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "recovery_token_hash",
                schema: "kernel_identity",
                table: "principals");
        }
    }
}
