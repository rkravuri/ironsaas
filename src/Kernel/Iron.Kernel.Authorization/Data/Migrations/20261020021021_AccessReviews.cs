using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_reviews",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "access_review_escalations",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    open_items = table.Column<int>(type: "integer", nullable: false),
                    days_overdue = table.Column<int>(type: "integer", nullable: false),
                    reviewers = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    targets = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_review_escalations", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_review_escalations_access_reviews_review_id",
                        column: x => x.review_id,
                        principalSchema: "kernel_authz",
                        principalTable: "access_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "access_review_items",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    role_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_path_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    unit_path_names = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_review_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_review_items_access_reviews_review_id",
                        column: x => x.review_id,
                        principalSchema: "kernel_authz",
                        principalTable: "access_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "access_review_units",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    includes_children = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_review_units", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_review_units_access_reviews_review_id",
                        column: x => x.review_id,
                        principalSchema: "kernel_authz",
                        principalTable: "access_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "append-only");

            migrationBuilder.CreateTable(
                name: "access_review_decisions",
                schema: "kernel_authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    assignment_removed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_review_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_review_decisions_access_review_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "kernel_authz",
                        principalTable: "access_review_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "append-only");

            migrationBuilder.CreateIndex(
                name: "ix_access_review_decisions_item_id",
                schema: "kernel_authz",
                table: "access_review_decisions",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_review_decisions_review_id",
                schema: "kernel_authz",
                table: "access_review_decisions",
                column: "review_id");

            migrationBuilder.CreateIndex(
                name: "ix_access_review_escalations_review_id_unit_id",
                schema: "kernel_authz",
                table: "access_review_escalations",
                columns: new[] { "review_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_review_items_review_id_assignment_id",
                schema: "kernel_authz",
                table: "access_review_items",
                columns: new[] { "review_id", "assignment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_review_items_review_id_unit_id",
                schema: "kernel_authz",
                table: "access_review_items",
                columns: new[] { "review_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_access_review_units_review_id_unit_id",
                schema: "kernel_authz",
                table: "access_review_units",
                columns: new[] { "review_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_reviews_status",
                schema: "kernel_authz",
                table: "access_reviews",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_review_decisions",
                schema: "kernel_authz");

            migrationBuilder.DropTable(
                name: "access_review_escalations",
                schema: "kernel_authz");

            migrationBuilder.DropTable(
                name: "access_review_units",
                schema: "kernel_authz");

            migrationBuilder.DropTable(
                name: "access_review_items",
                schema: "kernel_authz");

            migrationBuilder.DropTable(
                name: "access_reviews",
                schema: "kernel_authz");
        }
    }
}
