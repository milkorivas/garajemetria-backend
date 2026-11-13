using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarajeMetria.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class LprPersistence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OpenIddictApplications",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ApplicationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                ClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ClientType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                ConsentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                JsonWebKeySet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PostLogoutRedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Requirements = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Settings = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OpenIddictScopes",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Descriptions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Resources = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LprDevices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                HasCredentials = table.Column<bool>(type: "bit", nullable: false),
                Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AuthorizationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                OAuthApplicationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LprDevices", x => x.Id);
                table.UniqueConstraint("AK_LprDevices_Id_WorkshopId", x => new { x.Id, x.WorkshopId });
                table.CheckConstraint("CK_LprDevice_Identity", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [Version] <> '00000000-0000-0000-0000-000000000000' AND [AuthorizationVersion] <> '00000000-0000-0000-0000-000000000000'");
                table.CheckConstraint("CK_LprDevice_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
                table.CheckConstraint("CK_LprDevice_State", "[IsActive] = 1 OR [HasCredentials] = 0");
                table.CheckConstraint("CK_LprDevice_Utc", "DATEPART(TZOFFSET,[CreatedAt]) = 0 AND DATEPART(TZOFFSET,[UpdatedAt]) = 0");
                table.ForeignKey(
                    name: "FK_LprDevices_OpenIddictApplications_OAuthApplicationId",
                    column: x => x.OAuthApplicationId,
                    principalTable: "OpenIddictApplications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_LprDevices_WorkshopAccess_WorkshopId",
                    column: x => x.WorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "OpenIddictAuthorizations",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Scopes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                table.ForeignKey(
                    name: "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalTable: "OpenIddictApplications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "DeviceAdministrationAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Operation = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                PreviousDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PreviousWorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                PreviousName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                PreviousIsActive = table.Column<bool>(type: "bit", nullable: true),
                PreviousHasCredentials = table.Column<bool>(type: "bit", nullable: true),
                PreviousVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PreviousAuthorizationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PreviousCreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                PreviousUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                CurrentDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentWorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CurrentName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CurrentIsActive = table.Column<bool>(type: "bit", nullable: false),
                CurrentHasCredentials = table.Column<bool>(type: "bit", nullable: false),
                CurrentVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentAuthorizationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentCreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CurrentUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceAdministrationAudits", x => x.Id);
                table.CheckConstraint("CK_DeviceAudit_Operation", "[Operation] IN ('Created','Renamed','Deactivated','Reactivated','CredentialsRotated','CredentialsRevoked')");
                table.CheckConstraint("CK_DeviceAudit_Snapshot", "[CurrentDeviceId] = [DeviceId] AND [CurrentUpdatedAt] = [OccurredAt] AND (([Operation] = 'Created' AND [PreviousDeviceId] IS NULL) OR ([Operation] <> 'Created' AND [PreviousDeviceId] IS NOT NULL AND [PreviousVersion] IS NOT NULL AND [PreviousAuthorizationVersion] IS NOT NULL AND [PreviousWorkshopId] IS NOT NULL AND [PreviousName] IS NOT NULL AND [PreviousIsActive] IS NOT NULL AND [PreviousHasCredentials] IS NOT NULL AND [PreviousCreatedAt] IS NOT NULL AND [PreviousUpdatedAt] IS NOT NULL AND [PreviousDeviceId] = [DeviceId] AND [PreviousVersion] <> [CurrentVersion] AND [PreviousWorkshopId] = [CurrentWorkshopId]))");
                table.CheckConstraint("CK_DeviceAudit_Utc", "DATEPART(TZOFFSET,[OccurredAt]) = 0 AND DATEPART(TZOFFSET,[CurrentCreatedAt]) = 0 AND DATEPART(TZOFFSET,[CurrentUpdatedAt]) = 0 AND ([PreviousDeviceId] IS NULL OR (DATEPART(TZOFFSET,[PreviousCreatedAt]) = 0 AND DATEPART(TZOFFSET,[PreviousUpdatedAt]) = 0))");
                table.ForeignKey(
                    name: "FK_DeviceAdministrationAudits_LocalUsers_ActorId",
                    column: x => x.ActorId,
                    principalTable: "LocalUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_DeviceAdministrationAudits_LprDevices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "LprDevices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LprEvents",
            columns: table => new
            {
                EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                PlateOriginal = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PlateNormalized = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PlateStatus = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                PlateFormat = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                PlateRulesVersion = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                Confidence = table.Column<decimal>(type: "decimal(9,8)", precision: 9, scale: 8, nullable: false),
                Direction = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LprEvents", x => x.EventId);
                table.CheckConstraint("CK_LprEvent_Confidence", "[Confidence] >= 0 AND [Confidence] <= 1");
                table.CheckConstraint("CK_LprEvent_Direction", "[Direction] IS NULL OR [Direction] IN ('entry','exit')");
                table.CheckConstraint("CK_LprEvent_Plate", "LEN([PlateOriginal]) > 0 AND LEN([PlateNormalized]) > 0 AND LEN([PlateRulesVersion]) > 0 AND (([PlateStatus] = 'valid' AND [PlateFormat] IS NOT NULL AND LEN([PlateFormat]) > 0) OR ([PlateStatus] = 'invalid' AND [PlateFormat] IS NULL))");
                table.CheckConstraint("CK_LprEvent_Utc", "DATEPART(TZOFFSET,[CapturedAt]) = 0 AND DATEPART(TZOFFSET,[ReceivedAt]) = 0");
                table.CheckConstraint("CK_LprEvent_Uuid", "SUBSTRING(CONVERT(varchar(36),[EventId]),15,1) IN ('1','2','3','4','5','6','7','8') AND SUBSTRING(CONVERT(varchar(36),[EventId]),20,1) IN ('8','9','a','b','A','B')");
                table.ForeignKey(
                    name: "FK_LprEvents_LprDevices_DeviceId_WorkshopId",
                    columns: x => new { x.DeviceId, x.WorkshopId },
                    principalTable: "LprDevices",
                    principalColumns: new[] { "Id", "WorkshopId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_LprEvents_WorkshopAccess_WorkshopId",
                    column: x => x.WorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "OpenIddictTokens",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                AuthorizationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                ExpirationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RedemptionDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                Type = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalTable: "OpenIddictApplications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                    column: x => x.AuthorizationId,
                    principalTable: "OpenIddictAuthorizations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DeviceAdministrationAudits_ActorId",
            table: "DeviceAdministrationAudits",
            column: "ActorId");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceAdministrationAudits_DeviceId_OccurredAt",
            table: "DeviceAdministrationAudits",
            columns: new[] { "DeviceId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_DeviceAdministrationAudits_OccurredAt_Id",
            table: "DeviceAdministrationAudits",
            columns: new[] { "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_LprDevices_OAuthApplicationId",
            table: "LprDevices",
            column: "OAuthApplicationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LprDevices_WorkshopId_IsActive",
            table: "LprDevices",
            columns: new[] { "WorkshopId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_LprEvents_DeviceId_CapturedAt_EventId",
            table: "LprEvents",
            columns: new[] { "DeviceId", "CapturedAt", "EventId" });

        migrationBuilder.CreateIndex(
            name: "IX_LprEvents_DeviceId_WorkshopId",
            table: "LprEvents",
            columns: new[] { "DeviceId", "WorkshopId" });

        migrationBuilder.CreateIndex(
            name: "IX_LprEvents_WorkshopId_CapturedAt_EventId",
            table: "LprEvents",
            columns: new[] { "WorkshopId", "CapturedAt", "EventId" });

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictApplications_ClientId",
            table: "OpenIddictApplications",
            column: "ClientId",
            unique: true,
            filter: "[ClientId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
            table: "OpenIddictAuthorizations",
            columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictScopes_Name",
            table: "OpenIddictScopes",
            column: "Name",
            unique: true,
            filter: "[Name] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
            table: "OpenIddictTokens",
            columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictTokens_AuthorizationId",
            table: "OpenIddictTokens",
            column: "AuthorizationId");

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictTokens_ReferenceId",
            table: "OpenIddictTokens",
            column: "ReferenceId",
            unique: true,
            filter: "[ReferenceId] IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DeviceAdministrationAudits");

        migrationBuilder.DropTable(
            name: "LprEvents");

        migrationBuilder.DropTable(
            name: "OpenIddictScopes");

        migrationBuilder.DropTable(
            name: "OpenIddictTokens");

        migrationBuilder.DropTable(
            name: "LprDevices");

        migrationBuilder.DropTable(
            name: "OpenIddictAuthorizations");

        migrationBuilder.DropTable(
            name: "OpenIddictApplications");
    }
}
