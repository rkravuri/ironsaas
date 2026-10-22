using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "kernel_authz",
                table: "role_assignments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_source",
                schema: "kernel_authz",
                table: "role_assignments",
                column: "source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_role_assignments_source",
                schema: "kernel_authz",
                table: "role_assignments");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "kernel_authz",
                table: "role_assignments");
        }
    }
}
