using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImpersonationAndBreakGlass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "break_glass_until",
                schema: "kernel_identity",
                table: "principals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sign_in_links",
                schema: "kernel_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    impersonator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    impersonator_name = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    minutes = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sign_in_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_sign_in_links_principals_principal_id",
                        column: x => x.principal_id,
                        principalSchema: "kernel_identity",
                        principalTable: "principals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sign_in_links_principal_id",
                schema: "kernel_identity",
                table: "sign_in_links",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_sign_in_links_token_hash",
                schema: "kernel_identity",
                table: "sign_in_links",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sign_in_links",
                schema: "kernel_identity");

            migrationBuilder.DropColumn(
                name: "break_glass_until",
                schema: "kernel_identity",
                table: "principals");
        }
    }
}
