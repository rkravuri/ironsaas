using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionsConsoleAndRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "browser",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ended_by_id",
                schema: "kernel_identity",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "impersonation_ends_at",
                schema: "kernel_identity",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "impersonation_reason",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "impersonator_id",
                schema: "kernel_identity",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "impersonator_name",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "method",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "password");

            migrationBuilder.AddColumn<string>(
                name: "platform",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "return_session_id",
                schema: "kernel_identity",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "valid_after",
                schema: "kernel_identity",
                table: "principals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_revocations",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_revocations", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_created_at",
                schema: "kernel_identity",
                table: "sessions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_ended_at",
                schema: "kernel_identity",
                table: "sessions",
                column: "ended_at");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_impersonator_id",
                schema: "kernel_identity",
                table: "sessions",
                column: "impersonator_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_revocations_revoked_at",
                schema: "kernel_identity",
                table: "tenant_revocations",
                column: "revoked_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_revocations",
                schema: "kernel_identity");

            migrationBuilder.DropIndex(
                name: "ix_sessions_created_at",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_sessions_ended_at",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_sessions_impersonator_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "browser",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "city",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "ended_by_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "impersonation_ends_at",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "impersonation_reason",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "impersonator_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "impersonator_name",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "method",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "platform",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "return_session_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "valid_after",
                schema: "kernel_identity",
                table: "principals");
        }
    }
}
