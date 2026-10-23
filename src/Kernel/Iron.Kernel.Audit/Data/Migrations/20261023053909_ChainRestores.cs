using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChainRestores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chain_restores",
                schema: "kernel_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment = table.Column<int>(type: "integer", nullable: false),
                    start_position = table.Column<long>(type: "bigint", nullable: false),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    restore_position = table.Column<long>(type: "bigint", nullable: false),
                    restore_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lost_from = table.Column<long>(type: "bigint", nullable: false),
                    lost_to = table.Column<long>(type: "bigint", nullable: false),
                    ticket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    accepted_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chain_restores", x => x.id);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_chain_restores_segment",
                schema: "kernel_audit",
                table: "chain_restores",
                column: "segment",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chain_restores",
                schema: "kernel_audit");
        }
    }
}
