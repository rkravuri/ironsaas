using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Authorization.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewsOfVisibilityTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "subject",
                schema: "kernel_authz",
                table: "access_reviews",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "scope_kind",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "role_name",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<Guid>(
                name: "role_id",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "role_code",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<List<string>>(
                name: "held_tag_codes",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "held_tag_names",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "tag_code",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tag_id",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tag_name",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subject",
                schema: "kernel_authz",
                table: "access_reviews");

            migrationBuilder.DropColumn(
                name: "held_tag_codes",
                schema: "kernel_authz",
                table: "access_review_items");

            migrationBuilder.DropColumn(
                name: "held_tag_names",
                schema: "kernel_authz",
                table: "access_review_items");

            migrationBuilder.DropColumn(
                name: "tag_code",
                schema: "kernel_authz",
                table: "access_review_items");

            migrationBuilder.DropColumn(
                name: "tag_id",
                schema: "kernel_authz",
                table: "access_review_items");

            migrationBuilder.DropColumn(
                name: "tag_name",
                schema: "kernel_authz",
                table: "access_review_items");

            migrationBuilder.AlterColumn<string>(
                name: "scope_kind",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "role_name",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "role_id",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "role_code",
                schema: "kernel_authz",
                table: "access_review_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }
    }
}
