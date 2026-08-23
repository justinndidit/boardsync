using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC_JobsAndGitIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "git");

            migrationBuilder.CreateTable(
                name: "Installations",
                schema: "git",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WebhookSecret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Verification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EndpointToken = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "kernel",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    VisibleAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeasedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryLinks",
                schema: "git",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RepositoryName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryLinks_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalSchema: "git",
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                schema: "git",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProviderDeliveryId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Verification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalSchema: "git",
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_OrganizationId_Provider_ExternalId",
                schema: "git",
                table: "Installations",
                columns: new[] { "OrganizationId", "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Installations_Provider_EndpointToken",
                schema: "git",
                table: "Installations",
                columns: new[] { "Provider", "EndpointToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobId",
                schema: "kernel",
                table: "Jobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Runnable",
                schema: "kernel",
                table: "Jobs",
                columns: new[] { "Priority", "Sequence" },
                filter: "\"CompletedAt\" IS NULL AND \"DeadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryLinks_InstallationId_RepositoryExternalId",
                schema: "git",
                table: "RepositoryLinks",
                columns: new[] { "InstallationId", "RepositoryExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryLinks_InstallationId_RepositoryExternalId_Project~",
                schema: "git",
                table: "RepositoryLinks",
                columns: new[] { "InstallationId", "RepositoryExternalId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryLinks_ProjectId",
                schema: "git",
                table: "RepositoryLinks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_InstallationId_CreatedAt",
                schema: "git",
                table: "WebhookDeliveries",
                columns: new[] { "InstallationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_Provider_ProviderDeliveryId",
                schema: "git",
                table: "WebhookDeliveries",
                columns: new[] { "Provider", "ProviderDeliveryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "kernel");

            migrationBuilder.DropTable(
                name: "RepositoryLinks",
                schema: "git");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries",
                schema: "git");

            migrationBuilder.DropTable(
                name: "Installations",
                schema: "git");
        }
    }
}
