using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_role_assignments_principal_id_role_id_scope_kind_scope_id",
                schema: "kernel_authz",
                table: "role_assignments");

            migrationBuilder.AddColumn<string>(
                name: "scope_record_type",
                schema: "kernel_authz",
                table: "role_assignments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organisation_units",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "character varying(562)", maxLength: 562, nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organisation_units", x => x.id);
                    table.ForeignKey(
                        name: "fk_organisation_units_organisation_units_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "kernel_authz",
                        principalTable: "organisation_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_principal_id_role_id_scope_kind_scope_id_s",
                schema: "kernel_authz",
                table: "role_assignments",
                columns: new[] { "principal_id", "role_id", "scope_kind", "scope_id", "scope_record_type" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_organisation_units_code",
                schema: "kernel_authz",
                table: "organisation_units",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organisation_units_parent_id",
                schema: "kernel_authz",
                table: "organisation_units",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_organisation_units_path",
                schema: "kernel_authz",
                table: "organisation_units",
                column: "path",
                unique: true)
                .Annotation("Npgsql:IndexOperators", new[] { "varchar_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_organisation_units_root",
                schema: "kernel_authz",
                table: "organisation_units",
                column: "depth",
                unique: true,
                filter: "depth = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organisation_units",
                schema: "kernel_authz");

            migrationBuilder.DropIndex(
                name: "ix_role_assignments_principal_id_role_id_scope_kind_scope_id_s",
                schema: "kernel_authz",
                table: "role_assignments");

            migrationBuilder.DropColumn(
                name: "scope_record_type",
                schema: "kernel_authz",
                table: "role_assignments");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_principal_id_role_id_scope_kind_scope_id",
                schema: "kernel_authz",
                table: "role_assignments",
                columns: new[] { "principal_id", "role_id", "scope_kind", "scope_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
