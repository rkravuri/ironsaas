using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iron.Kernel.Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetiredFactorSecretColumnDropped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A unit whose authenticator secrets are still sealed under the retired factor key of T3 (ADR-0047) never had
            // the update whose seed moves them onto the key service (ADR-0054). Dropping the column would lose them, so the
            // migration refuses: the update stops at its migrate step, before the unit starts on this image, and nothing
            // in the database changes. RAISE's own code (P0001), which no client takes for a transient failure to retry.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    left_behind bigint;
                BEGIN
                    SELECT count(*) INTO left_behind FROM kernel_identity.factors WHERE secret_ciphertext IS NOT NULL;
                    IF left_behind > 0 THEN
                        RAISE EXCEPTION '% authenticator app secrets are still sealed under the retired factor key. Update this unit first with a release from v2026.10.28.43 up to the last before this one, whose seed moves them onto the key service (ADR-0054); then this update.', left_behind;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "secret_ciphertext",
                schema: "kernel_identity",
                table: "factors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "secret_ciphertext",
                schema: "kernel_identity",
                table: "factors",
                type: "bytea",
                nullable: true);
        }
    }
}
