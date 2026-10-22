using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmergencyAccountsAndTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "emergency",
                schema: "kernel_identity",
                table: "principals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "document_acceptances",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_acceptances", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_acceptances_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "tenant_documents",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_documents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_acceptances_principal_id_source_document_key_versi",
                schema: "kernel_identity",
                table: "document_acceptances",
                columns: new[] { "principal_id", "source", "document_key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_acceptances_source_document_key_version",
                schema: "kernel_identity",
                table: "document_acceptances",
                columns: new[] { "source", "document_key", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_documents_key_number",
                schema: "kernel_identity",
                table: "tenant_documents",
                columns: new[] { "key", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_acceptances",
                schema: "kernel_identity");

            migrationBuilder.DropTable(
                name: "tenant_documents",
                schema: "kernel_identity");

            migrationBuilder.DropColumn(
                name: "emergency",
                schema: "kernel_identity",
                table: "principals");
        }
    }
}
