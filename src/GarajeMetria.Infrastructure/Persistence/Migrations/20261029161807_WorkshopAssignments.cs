using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarajeMetria.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WorkshopAssignments : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "AssignmentVersion",
            table: "LocalUsers",
            type: "uniqueidentifier",
            nullable: true);

        // Preserve every existing profile and give each assignment its own opaque version.
        migrationBuilder.Sql("UPDATE [LocalUsers] SET [AssignmentVersion] = NEWID();");
        migrationBuilder.AlterColumn<Guid>(
            name: "AssignmentVersion",
            table: "LocalUsers",
            type: "uniqueidentifier",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uniqueidentifier",
            oldNullable: true);

        migrationBuilder.CreateTable(
            name: "WorkshopAssignmentAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PreviousWorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NewWorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CorrelationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkshopAssignmentAudits", x => x.Id);
                table.ForeignKey(
                    name: "FK_WorkshopAssignmentAudits_LocalUsers_ActorId",
                    column: x => x.ActorId,
                    principalTable: "LocalUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_WorkshopAssignmentAudits_LocalUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "LocalUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_WorkshopAssignmentAudits_WorkshopAccess_NewWorkshopId",
                    column: x => x.NewWorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_WorkshopAssignmentAudits_WorkshopAccess_PreviousWorkshopId",
                    column: x => x.PreviousWorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkshopAssignmentAudits_ActorId",
            table: "WorkshopAssignmentAudits",
            column: "ActorId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkshopAssignmentAudits_NewWorkshopId",
            table: "WorkshopAssignmentAudits",
            column: "NewWorkshopId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkshopAssignmentAudits_OccurredAt",
            table: "WorkshopAssignmentAudits",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_WorkshopAssignmentAudits_PreviousWorkshopId",
            table: "WorkshopAssignmentAudits",
            column: "PreviousWorkshopId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkshopAssignmentAudits_UserId_OccurredAt",
            table: "WorkshopAssignmentAudits",
            columns: new[] { "UserId", "OccurredAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WorkshopAssignmentAudits");

        migrationBuilder.DropColumn(
            name: "AssignmentVersion",
            table: "LocalUsers");
    }
}
