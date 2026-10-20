using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class Delegations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delegations",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delegator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delegate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duties = table.Column<List<string>>(type: "text[]", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delegations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delegations_delegate_id_ends_at",
                schema: "kernel_authz",
                table: "delegations",
                columns: new[] { "delegate_id", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "ix_delegations_delegator_id_ends_at",
                schema: "kernel_authz",
                table: "delegations",
                columns: new[] { "delegator_id", "ends_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delegations",
                schema: "kernel_authz");
        }
    }
}
