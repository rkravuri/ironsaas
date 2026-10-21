using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecondFactors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "authenticated_at",
                schema: "kernel_identity",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "second_factor",
                schema: "kernel_identity",
                table: "sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mfa_required_since",
                schema: "kernel_identity",
                table: "principals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "second_factor_cooling_until",
                schema: "kernel_identity",
                table: "principals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "second_factor_failures",
                schema: "kernel_identity",
                table: "principals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "authentication_challenges",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    web_authn_options = table.Column<string>(type: "text", nullable: true),
                    pending_secret = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authentication_challenges", x => x.id);
                    table.ForeignKey(
                        name: "fk_authentication_challenges_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_authentication_challenges_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "kernel_identity",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "factors",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    last_used_step = table.Column<long>(type: "bigint", nullable: true),
                    credential_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    sign_count = table.Column<long>(type: "bigint", nullable: false),
                    user_handle = table.Column<byte[]>(type: "bytea", nullable: true),
                    aa_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_factors", x => x.id);
                    table.ForeignKey(
                        name: "fk_factors_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recovery_codes",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_recovery_codes_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authentication_challenges_expires_at",
                schema: "kernel_identity",
                table: "authentication_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_challenges_principal_id",
                schema: "kernel_identity",
                table: "authentication_challenges",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_challenges_session_id",
                schema: "kernel_identity",
                table: "authentication_challenges",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_challenges_token_hash",
                schema: "kernel_identity",
                table: "authentication_challenges",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_factors_credential_id",
                schema: "kernel_identity",
                table: "factors",
                column: "credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_factors_principal_id",
                schema: "kernel_identity",
                table: "factors",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_factors_user_handle",
                schema: "kernel_identity",
                table: "factors",
                column: "user_handle");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_codes_code_hash",
                schema: "kernel_identity",
                table: "recovery_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recovery_codes_principal_id",
                schema: "kernel_identity",
                table: "recovery_codes",
                column: "principal_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authentication_challenges",
                schema: "kernel_identity");

            migrationBuilder.DropTable(
                name: "factors",
                schema: "kernel_identity");

            migrationBuilder.DropTable(
                name: "recovery_codes",
                schema: "kernel_identity");

            migrationBuilder.DropColumn(
                name: "authenticated_at",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "second_factor",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "mfa_required_since",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "second_factor_cooling_until",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "second_factor_failures",
                schema: "kernel_identity",
                table: "principals");
        }
    }
}
