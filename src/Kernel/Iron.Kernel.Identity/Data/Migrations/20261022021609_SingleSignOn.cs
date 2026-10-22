using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class SingleSignOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "provider_id",
                schema: "kernel_identity",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sso_only",
                schema: "kernel_identity",
                table: "principals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "exchange",
                schema: "kernel_identity",
                table: "authentication_challenges",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provider_id",
                schema: "kernel_identity",
                table: "authentication_challenges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sso_providers",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    preset = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    discovery_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    client_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    allowed_domains = table.Column<List<string>>(type: "text[]", nullable: false),
                    email_claim = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_claim = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    groups_claim = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    just_in_time = table.Column<bool>(type: "boolean", nullable: false),
                    default_role_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    default_home_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sso_only = table.Column<bool>(type: "boolean", nullable: false),
                    trust_provider_mfa = table.Column<bool>(type: "boolean", nullable: false),
                    role_mappings = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sso_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sso_links",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    last_sign_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sso_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_sso_links_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sso_links_sso_providers_provider_id",
                        column: x => x.provider_id,
                        principalSchema: "kernel_identity",
                        principalTable: "sso_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_provider_id",
                schema: "kernel_identity",
                table: "sessions",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_challenges_provider_id",
                schema: "kernel_identity",
                table: "authentication_challenges",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_sso_links_principal_id",
                schema: "kernel_identity",
                table: "sso_links",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_sso_links_provider_id_principal_id",
                schema: "kernel_identity",
                table: "sso_links",
                columns: new[] { "provider_id", "principal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sso_links_provider_id_subject",
                schema: "kernel_identity",
                table: "sso_links",
                columns: new[] { "provider_id", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sso_providers_name",
                schema: "kernel_identity",
                table: "sso_providers",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_authentication_challenges_sso_providers_provider_id",
                schema: "kernel_identity",
                table: "authentication_challenges",
                column: "provider_id",
                principalSchema: "kernel_identity",
                principalTable: "sso_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_sessions_sso_providers_provider_id",
                schema: "kernel_identity",
                table: "sessions",
                column: "provider_id",
                principalSchema: "kernel_identity",
                principalTable: "sso_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_authentication_challenges_sso_providers_provider_id",
                schema: "kernel_identity",
                table: "authentication_challenges");

            migrationBuilder.DropForeignKey(
                name: "fk_sessions_sso_providers_provider_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropTable(
                name: "sso_links",
                schema: "kernel_identity");

            migrationBuilder.DropTable(
                name: "sso_providers",
                schema: "kernel_identity");

            migrationBuilder.DropIndex(
                name: "ix_sessions_provider_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_authentication_challenges_provider_id",
                schema: "kernel_identity",
                table: "authentication_challenges");

            migrationBuilder.DropColumn(
                name: "provider_id",
                schema: "kernel_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "sso_only",
                schema: "kernel_identity",
                table: "principals");

            migrationBuilder.DropColumn(
                name: "exchange",
                schema: "kernel_identity",
                table: "authentication_challenges");

            migrationBuilder.DropColumn(
                name: "provider_id",
                schema: "kernel_identity",
                table: "authentication_challenges");
        }
    }
}
