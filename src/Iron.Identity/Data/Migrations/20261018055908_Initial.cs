using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "principals",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_id = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    locale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    failed_login_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_failed_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invitation_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    invitation_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_principals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_sessions_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_principals_invitation_token_hash",
                schema: "identity",
                table: "principals",
                column: "invitation_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_principals_login_id",
                schema: "identity",
                table: "principals",
                column: "login_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_principals_status",
                schema: "identity",
                table: "principals",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_ended_at_last_activity_at",
                schema: "identity",
                table: "sessions",
                columns: new[] { "ended_at", "last_activity_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_principal_id",
                schema: "identity",
                table: "sessions",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_token_hash",
                schema: "identity",
                table: "sessions",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sessions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "principals",
                schema: "identity");
        }
    }
}
