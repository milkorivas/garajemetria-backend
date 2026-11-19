using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarajeMetria.Infrastructure.Persistence.Migrations;
/// <inheritdoc />
public partial class VehicleHistoryQueries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "StayOrderKey",
            table: "VehicleStays",
            type: "binary(16)",
            nullable: true,
            computedColumnSql: "CONVERT(binary(16), REPLACE(CONVERT(char(36), [Id]), '-', ''), 2)",
            stored: true);

        migrationBuilder.CreateTable(
            name: "HistoryAccessAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                Operation = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                ScopeKind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RowCount = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HistoryAccessAudits", x => x.Id);
                table.CheckConstraint("CK_HistoryAudit_Rows", "[RowCount] >= 0");
                table.CheckConstraint("CK_HistoryAudit_Scope", "[ScopeKind] IN ('Selected', 'AllAuthorized')");
                table.CheckConstraint("CK_HistoryAudit_Status", "[Status] = 'AuthorizedRelease'");
                table.ForeignKey(
                    name: "FK_HistoryAccessAudits_LocalUsers_ActorUserId",
                    column: x => x.ActorUserId,
                    principalTable: "LocalUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "HistoryExportLeases",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Owner = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HistoryExportLeases", x => x.UserId);
            });

        migrationBuilder.CreateTable(
            name: "HistoryAccessAuditWorkshops",
            columns: table => new
            {
                AuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HistoryAccessAuditWorkshops", x => new { x.AuditId, x.WorkshopId });
                table.ForeignKey(
                    name: "FK_HistoryAccessAuditWorkshops_HistoryAccessAudits_AuditId",
                    column: x => x.AuditId,
                    principalTable: "HistoryAccessAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_HistoryAccessAuditWorkshops_WorkshopAccess_WorkshopId",
                    column: x => x.WorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_WorkshopId_ExitAt_StayOrderKey",
            table: "VehicleStays",
            columns: new[] { "WorkshopId", "ExitAt", "StayOrderKey" },
            filter: "[AbsorbedByStayId] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_WorkshopId_State_EntryAt_StayOrderKey",
            table: "VehicleStays",
            columns: new[] { "WorkshopId", "State", "EntryAt", "StayOrderKey" },
            filter: "[AbsorbedByStayId] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_LprEvents_WorkshopId_ReceivedAt_EventOrderKey",
            table: "LprEvents",
            columns: new[] { "WorkshopId", "ReceivedAt", "EventOrderKey" });

        migrationBuilder.CreateIndex(
            name: "IX_HistoryAccessAudits_ActorUserId_OccurredAt_Id",
            table: "HistoryAccessAudits",
            columns: new[] { "ActorUserId", "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_HistoryAccessAudits_OccurredAt_Id",
            table: "HistoryAccessAudits",
            columns: new[] { "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_HistoryAccessAuditWorkshops_WorkshopId",
            table: "HistoryAccessAuditWorkshops",
            column: "WorkshopId");

        migrationBuilder.CreateIndex(
            name: "IX_HistoryExportLeases_ExpiresAt",
            table: "HistoryExportLeases",
            column: "ExpiresAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [HistoryAccessAudits]) THROW 51001, 'History audit preservation requires an explicit recovery plan.', 1;");
        migrationBuilder.DropTable(
            name: "HistoryAccessAuditWorkshops");

        migrationBuilder.DropTable(
            name: "HistoryExportLeases");

        migrationBuilder.DropTable(
            name: "HistoryAccessAudits");

        migrationBuilder.DropIndex(
            name: "IX_VehicleStays_WorkshopId_ExitAt_StayOrderKey",
            table: "VehicleStays");

        migrationBuilder.DropIndex(
            name: "IX_VehicleStays_WorkshopId_State_EntryAt_StayOrderKey",
            table: "VehicleStays");

        migrationBuilder.DropIndex(
            name: "IX_LprEvents_WorkshopId_ReceivedAt_EventOrderKey",
            table: "LprEvents");

        migrationBuilder.DropColumn(
            name: "StayOrderKey",
            table: "VehicleStays");
    }
}
