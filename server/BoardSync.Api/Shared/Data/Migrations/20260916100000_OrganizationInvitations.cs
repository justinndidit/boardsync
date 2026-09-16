using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Membership becomes something a person accepts rather than something done to them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is keyed on an email, not a user id, because at the moment an invitation is
    /// created there may be no user to key on — which is exactly the case the previous flow could
    /// not express: it looked the address up and gave up with a 404 if nobody held it.
    /// </para>
    /// <para>
    /// Written by hand — the <c>dotnet ef</c> tool on this machine targets a runtime newer than the
    /// one installed — but the shape matches what the scaffolder produces from the model, and the
    /// accompanying Designer snapshot is the model as of this migration.
    /// </para>
    /// </remarks>
    public partial class OrganizationInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationInvitations",
                schema: "org",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationInvitations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "org",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The only way in from a link, so it is unique as well as indexed: two rows sharing a
            // hash would make which organization a link joins depend on row order.
            migrationBuilder.CreateIndex(
                name: "IX_OrganizationInvitations_TokenHash",
                schema: "org",
                table: "OrganizationInvitations",
                column: "TokenHash",
                unique: true);

            // The admin-side listing: this organization's invitations, newest first.
            migrationBuilder.CreateIndex(
                name: "IX_OrganizationInvitations_OrganizationId_CreatedAt",
                schema: "org",
                table: "OrganizationInvitations",
                columns: new[] { "OrganizationId", "CreatedAt" },
                descending: new[] { false, true });

            /*
             * Not unique. An expired or revoked invitation is history worth keeping, and a second
             * one to the same address is how re-sending works. "Only one open invitation per
             * address" is a rule about state, so it lives in the service, which can see the state.
             */
            migrationBuilder.CreateIndex(
                name: "IX_OrganizationInvitations_OrganizationId_Email",
                schema: "org",
                table: "OrganizationInvitations",
                columns: new[] { "OrganizationId", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationInvitations",
                schema: "org");
        }
    }
}
