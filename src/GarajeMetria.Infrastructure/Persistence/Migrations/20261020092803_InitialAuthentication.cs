using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarajeMetria.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialAuthentication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuthenticationCleanup",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Owner = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                NextRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthenticationCleanup", x => x.Id);
                table.CheckConstraint("CK_AuthenticationCleanup_Singleton", "[Id] = 1");
            });

        migrationBuilder.CreateTable(
            name: "AuthenticationRateConfiguration",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthenticationRateConfiguration", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AuthenticationRatePartitions",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthenticationRatePartitions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkshopAccess",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkshopAccess", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkshopCatalogState",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                LastPublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkshopCatalogState", x => x.Id);
                table.CheckConstraint("CK_WorkshopCatalogState_Singleton", "[Id] = 1");
            });

        migrationBuilder.CreateTable(
            name: "AuthenticationRatePermits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartitionId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthenticationRatePermits", x => x.Id);
                table.ForeignKey(
                    name: "FK_AuthenticationRatePermits_AuthenticationRatePartitions_PartitionId",
                    column: x => x.PartitionId,
                    principalTable: "AuthenticationRatePartitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LocalUsers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DirectoryObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Username = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                DefaultWorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LocalUsers", x => x.Id);
                table.ForeignKey(
                    name: "FK_LocalUsers_WorkshopAccess_DefaultWorkshopId",
                    column: x => x.DefaultWorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "AuthenticationSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                CloseReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthenticationSessions", x => x.Id);
                table.CheckConstraint("CK_Session_Lifetime", "[AbsoluteExpiresAt] > [StartedAt]");
                table.ForeignKey(
                    name: "FK_AuthenticationSessions_LocalUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "LocalUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ProvisioningAudit",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProvisioningAudit", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProvisioningAudit_LocalUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "LocalUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RefreshTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(0)", precision: 0, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(0)", precision: 0, nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                table.CheckConstraint("CK_Refresh_Lifetime", "[ExpiresAt] > [IssuedAt]");
                table.ForeignKey(
                    name: "FK_RefreshTokens_AuthenticationSessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "AuthenticationSessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuthenticationRatePermits_PartitionId_AcceptedAt",
            table: "AuthenticationRatePermits",
            columns: new[] { "PartitionId", "AcceptedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AuthenticationSessions_AbsoluteExpiresAt",
            table: "AuthenticationSessions",
            column: "AbsoluteExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_AuthenticationSessions_ClosedAt",
            table: "AuthenticationSessions",
            column: "ClosedAt");

        migrationBuilder.CreateIndex(
            name: "IX_AuthenticationSessions_UserId",
            table: "AuthenticationSessions",
            column: "UserId",
            unique: true,
            filter: "[ClosedAt] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_LocalUsers_DefaultWorkshopId",
            table: "LocalUsers",
            column: "DefaultWorkshopId");

        migrationBuilder.CreateIndex(
            name: "IX_LocalUsers_DirectoryObjectId",
            table: "LocalUsers",
            column: "DirectoryObjectId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProvisioningAudit_OccurredAt",
            table: "ProvisioningAudit",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_ProvisioningAudit_UserId",
            table: "ProvisioningAudit",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_SessionId",
            table: "RefreshTokens",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_TokenHash",
            table: "RefreshTokens",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuthenticationCleanup");

        migrationBuilder.DropTable(
            name: "AuthenticationRateConfiguration");

        migrationBuilder.DropTable(
            name: "AuthenticationRatePermits");

        migrationBuilder.DropTable(
            name: "ProvisioningAudit");

        migrationBuilder.DropTable(
            name: "RefreshTokens");

        migrationBuilder.DropTable(
            name: "WorkshopCatalogState");

        migrationBuilder.DropTable(
            name: "AuthenticationRatePartitions");

        migrationBuilder.DropTable(
            name: "AuthenticationSessions");

        migrationBuilder.DropTable(
            name: "LocalUsers");

        migrationBuilder.DropTable(
            name: "WorkshopAccess");
    }
}
