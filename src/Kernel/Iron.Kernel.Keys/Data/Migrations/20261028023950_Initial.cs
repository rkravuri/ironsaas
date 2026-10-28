using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Keys.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel_keys");

            migrationBuilder.CreateTable(
                name: "key_checks",
                schema: "kernel_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    value_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_checks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "key_rotations",
                schema: "kernel_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    from_version = table.Column<int>(type: "integer", nullable: false),
                    to_version = table.Column<int>(type: "integer", nullable: false),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finish_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_rotations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "key_states",
                schema: "kernel_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    minimum_version = table.Column<int>(type: "integer", nullable: false),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_rotation_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotation_days = table.Column<int>(type: "integer", nullable: false),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_key_checks_value_hash",
                schema: "kernel_keys",
                table: "key_checks",
                column: "value_hash");

            migrationBuilder.CreateIndex(
                name: "ix_key_rotations_key_rotated_at",
                schema: "kernel_keys",
                table: "key_rotations",
                columns: new[] { "key", "rotated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_key_states_key",
                schema: "kernel_keys",
                table: "key_states",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "key_checks",
                schema: "kernel_keys");

            migrationBuilder.DropTable(
                name: "key_rotations",
                schema: "kernel_keys");

            migrationBuilder.DropTable(
                name: "key_states",
                schema: "kernel_keys");
        }
    }
}
