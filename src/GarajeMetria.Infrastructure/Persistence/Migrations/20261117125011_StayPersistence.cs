using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarajeMetria.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class StayPersistence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF;");
        migrationBuilder.DropCheckConstraint(
            name: "CK_DeviceAudit_Operation",
            table: "DeviceAdministrationAudits");

        migrationBuilder.AddColumn<Guid>(
            name: "CurrentLocationId",
            table: "LprDevices",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CurrentLocationDescription",
            table: "DeviceAdministrationAudits",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CurrentLocationId",
            table: "DeviceAdministrationAudits",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CurrentLocationRecordedAt",
            table: "DeviceAdministrationAudits",
            type: "datetimeoffset(7)",
            precision: 7,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PreviousLocationDescription",
            table: "DeviceAdministrationAudits",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PreviousLocationId",
            table: "DeviceAdministrationAudits",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PreviousLocationRecordedAt",
            table: "DeviceAdministrationAudits",
            type: "datetimeoffset(7)",
            precision: 7,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "EventOrderKey",
            table: "LprEvents",
            type: "binary(16)",
            nullable: false,
            computedColumnSql: "CONVERT(binary(16), REPLACE(CONVERT(char(36), [EventId]), '-', ''), 2)",
            stored: true);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_LprEvents_EventId_DeviceId_WorkshopId",
            table: "LprEvents",
            columns: new[] { "EventId", "DeviceId", "WorkshopId" });

        migrationBuilder.AddUniqueConstraint(
            name: "AK_LprEvents_EventId_WorkshopId_PlateNormalized",
            table: "LprEvents",
            columns: new[] { "EventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateTable(
            name: "LprDeviceLocations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CreationOrder = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LprDeviceLocations", x => x.Id);
                table.UniqueConstraint("AK_LprDeviceLocations_Id_DeviceId_WorkshopId", x => new { x.Id, x.DeviceId, x.WorkshopId });
                table.CheckConstraint("CK_DeviceLocation_Description", "LEN(LTRIM(RTRIM([Description]))) > 0");
                table.CheckConstraint("CK_DeviceLocation_Identity", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [CreationOrder] > 0");
                table.CheckConstraint("CK_DeviceLocation_Utc", "DATEPART(TZOFFSET,[RecordedAt]) = 0");
                table.ForeignKey(
                    name: "FK_LprDeviceLocations_LprDevices_DeviceId_WorkshopId",
                    columns: x => new { x.DeviceId, x.WorkshopId },
                    principalTable: "LprDevices",
                    principalColumns: new[] { "Id", "WorkshopId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StayPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                EstimatedDurationTicks = table.Column<long>(type: "bigint", nullable: false),
                RepeatWindowTicks = table.Column<long>(type: "bigint", nullable: false),
                MaximumAssociationTicks = table.Column<long>(type: "bigint", nullable: false),
                TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                AlgorithmVersion = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StayPolicies", x => x.Id);
                table.CheckConstraint("CK_StayPolicy_Identity", "[Id] <> '00000000-0000-0000-0000-000000000000' AND LEN([Fingerprint]) = 64");
                table.CheckConstraint("CK_StayPolicy_Values", "[EstimatedDurationTicks] > 0 AND [RepeatWindowTicks] > 0 AND [MaximumAssociationTicks] > 0 AND [AlgorithmVersion] > 0");
                table.CheckConstraint("CK_StayPolicy_Zone", "[TimeZoneId] = N'Atlantic/Canary'");
            });

        migrationBuilder.CreateTable(
            name: "VehicleStayPartitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PlateNormalized = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                InputRevision = table.Column<long>(type: "bigint", nullable: false),
                ProjectionRevision = table.Column<long>(type: "bigint", nullable: false),
                LastCreationOrder = table.Column<long>(type: "bigint", nullable: false),
                NextClosureAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                PlateIdentityKey = table.Column<byte[]>(type: "varbinary(64)", nullable: true, computedColumnSql: "CONVERT(varbinary(64), [PlateNormalized])", stored: true),
                WorkshopIdentityKey = table.Column<byte[]>(type: "varbinary(512)", nullable: true, computedColumnSql: "CONVERT(varbinary(512), [WorkshopId])", stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VehicleStayPartitions", x => x.Id);
                table.UniqueConstraint("AK_VehicleStayPartitions_Id_WorkshopId_PlateNormalized", x => new { x.Id, x.WorkshopId, x.PlateNormalized });
                table.CheckConstraint("CK_StayPartition_Identity", "[Id] <> '00000000-0000-0000-0000-000000000000' AND LEN(LTRIM(RTRIM([WorkshopId]))) > 0 AND LEN(LTRIM(RTRIM([PlateNormalized]))) > 0");
                table.CheckConstraint("CK_StayPartition_Revisions", "[InputRevision] >= 0 AND [ProjectionRevision] >= 0 AND [LastCreationOrder] >= 0");
                table.CheckConstraint("CK_StayPartition_Utc", "[NextClosureAt] IS NULL OR DATEPART(TZOFFSET,[NextClosureAt]) = 0");
                table.ForeignKey(
                    name: "FK_VehicleStayPartitions_WorkshopAccess_WorkshopId",
                    column: x => x.WorkshopId,
                    principalTable: "WorkshopAccess",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LprObservationLocations",
            columns: table => new
            {
                EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Provenance = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LprObservationLocations", x => x.EventId);
                table.CheckConstraint("CK_ObservationLocation_Provenance", "([LocationId] IS NULL AND [Provenance] = 'Unknown') OR ([LocationId] IS NOT NULL AND [Provenance] IN ('AtCapture','LatestKnownForFutureCapture'))");
                table.ForeignKey(
                    name: "FK_LprObservationLocations_LprDeviceLocations_LocationId_DeviceId_WorkshopId",
                    columns: x => new { x.LocationId, x.DeviceId, x.WorkshopId },
                    principalTable: "LprDeviceLocations",
                    principalColumns: new[] { "Id", "DeviceId", "WorkshopId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_LprObservationLocations_LprEvents_EventId_DeviceId_WorkshopId",
                    columns: x => new { x.EventId, x.DeviceId, x.WorkshopId },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "DeviceId", "WorkshopId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StayConfiguration",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                ActivePolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StayConfiguration", x => x.Id);
                table.CheckConstraint("CK_StayConfiguration_Singleton", "[Id] = 1");
                table.CheckConstraint("CK_StayConfiguration_Utc", "DATEPART(TZOFFSET,[ActivatedAt]) = 0");
                table.CheckConstraint("CK_StayConfiguration_Version", "[Version] <> '00000000-0000-0000-0000-000000000000'");
                table.ForeignKey(
                    name: "FK_StayConfiguration_StayPolicies_ActivePolicyId",
                    column: x => x.ActivePolicyId,
                    principalTable: "StayPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StayDayClosures",
            columns: table => new
            {
                PartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                ProcessedInputRevision = table.Column<long>(type: "bigint", nullable: false),
                ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                NextCheckAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StayDayClosures", x => new { x.PartitionId, x.LocalDate });
                table.CheckConstraint("CK_StayClosure_Revision", "[ProcessedInputRevision] >= 0");
                table.CheckConstraint("CK_StayClosure_Utc", "DATEPART(TZOFFSET,[ClosedAt]) = 0 AND ([NextCheckAt] IS NULL OR DATEPART(TZOFFSET,[NextCheckAt]) = 0)");
                table.ForeignKey(
                    name: "FK_StayDayClosures_VehicleStayPartitions_PartitionId",
                    column: x => x.PartitionId,
                    principalTable: "VehicleStayPartitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StayProjectionRevisions",
            columns: table => new
            {
                PartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PlateNormalized = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                InputRevision = table.Column<long>(type: "bigint", nullable: false),
                Cause = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CausingEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                FormatVersion = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StayProjectionRevisions", x => new { x.PartitionId, x.Revision });
                table.CheckConstraint("CK_StayRevision_Cause", "([Cause] = 'Ingestion' AND [CausingEventId] IS NOT NULL) OR ([Cause] IN ('DailyClosure','InitialProjection') AND [CausingEventId] IS NULL)");
                table.CheckConstraint("CK_StayRevision_Utc", "DATEPART(TZOFFSET,[EvaluatedAt]) = 0");
                table.CheckConstraint("CK_StayRevision_Values", "[Revision] > 0 AND [InputRevision] >= 0 AND [FormatVersion] > 0 AND ISJSON([ChangesJson]) = 1");
                table.ForeignKey(
                    name: "FK_StayProjectionRevisions_LprEvents_CausingEventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.CausingEventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayProjectionRevisions_VehicleStayPartitions_PartitionId_WorkshopId_PlateNormalized",
                    columns: x => new { x.PartitionId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "VehicleStayPartitions",
                    principalColumns: new[] { "Id", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "VehicleStays",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PlateNormalized = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OriginEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CreationOrder = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                State = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                EntryAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                EntrySource = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                EntryEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExitAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                ExitSource = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                ExitEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                AbsorbedByStayId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                NextClosureAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VehicleStays", x => x.Id);
                table.UniqueConstraint("AK_VehicleStays_PartitionId_Id", x => new { x.PartitionId, x.Id });
                table.CheckConstraint("CK_Stay_Chronology", "[UpdatedAt] >= [CreatedAt] AND ([EntryAt] IS NULL OR [ExitAt] IS NULL OR [ExitAt] >= [EntryAt])");
                table.CheckConstraint("CK_Stay_Entry", "([EntryAt] IS NULL AND [EntrySource] IS NULL AND [EntryEventId] IS NULL) OR ([EntryAt] IS NOT NULL AND [EntrySource] IS NOT NULL AND (([EntrySource] = 'Estimated' AND [EntryEventId] IS NULL) OR ([EntrySource] IN ('Explicit','Inferred') AND [EntryEventId] IS NOT NULL)))");
                table.CheckConstraint("CK_Stay_Estimation", "([EntrySource] IS NULL OR [EntrySource] <> 'Estimated' OR ([ExitEventId] IS NOT NULL AND [ExitSource] IN ('Explicit','Inferred'))) AND ([ExitSource] IS NULL OR [ExitSource] <> 'Estimated' OR ([EntryEventId] IS NOT NULL AND [EntrySource] IN ('Explicit','Inferred')))");
                table.CheckConstraint("CK_Stay_Exit", "([ExitAt] IS NULL AND [ExitSource] IS NULL AND [ExitEventId] IS NULL) OR ([ExitAt] IS NOT NULL AND [ExitSource] IS NOT NULL AND (([ExitSource] = 'Estimated' AND [ExitEventId] IS NULL) OR ([ExitSource] IN ('Explicit','Inferred') AND [ExitEventId] IS NOT NULL)))");
                table.CheckConstraint("CK_Stay_Identity", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [CreationOrder] > 0 AND [Revision] > 0 AND ([AbsorbedByStayId] IS NULL OR [AbsorbedByStayId] <> [Id])");
                table.CheckConstraint("CK_Stay_State", "([State] = 'Open' AND [EntryAt] IS NOT NULL AND [ExitAt] IS NULL) OR ([State] = 'PendingEntry' AND [EntryAt] IS NULL AND [ExitAt] IS NOT NULL) OR ([State] = 'ClosedProvisional' AND [EntryAt] IS NOT NULL AND [ExitAt] IS NOT NULL)");
                table.CheckConstraint("CK_Stay_Utc", "DATEPART(TZOFFSET,[CreatedAt]) = 0 AND DATEPART(TZOFFSET,[UpdatedAt]) = 0 AND ([EntryAt] IS NULL OR DATEPART(TZOFFSET,[EntryAt]) = 0) AND ([ExitAt] IS NULL OR DATEPART(TZOFFSET,[ExitAt]) = 0) AND ([NextClosureAt] IS NULL OR DATEPART(TZOFFSET,[NextClosureAt]) = 0)");
                table.ForeignKey(
                    name: "FK_VehicleStays_LprEvents_EntryEventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.EntryEventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleStays_LprEvents_ExitEventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.ExitEventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleStays_LprEvents_OriginEventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.OriginEventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleStays_StayPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "StayPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleStays_StayProjectionRevisions_PartitionId_Revision",
                    columns: x => new { x.PartitionId, x.Revision },
                    principalTable: "StayProjectionRevisions",
                    principalColumns: new[] { "PartitionId", "Revision" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleStays_VehicleStayPartitions_PartitionId_WorkshopId_PlateNormalized",
                    columns: x => new { x.PartitionId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "VehicleStayPartitions",
                    principalColumns: new[] { "Id", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleStays_VehicleStays_PartitionId_AbsorbedByStayId",
                    columns: x => new { x.PartitionId, x.AbsorbedByStayId },
                    principalTable: "VehicleStays",
                    principalColumns: new[] { "PartitionId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StayLineage",
            columns: table => new
            {
                PartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                SourceStayId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TargetStayId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Kind = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StayLineage", x => new { x.PartitionId, x.Revision, x.Kind, x.SourceStayId, x.TargetStayId });
                table.CheckConstraint("CK_StayLineage_Kind", "[Kind] IN ('Fusion','Split') AND [SourceStayId] <> [TargetStayId]");
                table.CheckConstraint("CK_StayLineage_Utc", "DATEPART(TZOFFSET,[RecordedAt]) = 0");
                table.ForeignKey(
                    name: "FK_StayLineage_StayProjectionRevisions_PartitionId_Revision",
                    columns: x => new { x.PartitionId, x.Revision },
                    principalTable: "StayProjectionRevisions",
                    principalColumns: new[] { "PartitionId", "Revision" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayLineage_VehicleStays_PartitionId_SourceStayId",
                    columns: x => new { x.PartitionId, x.SourceStayId },
                    principalTable: "VehicleStays",
                    principalColumns: new[] { "PartitionId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayLineage_VehicleStays_PartitionId_TargetStayId",
                    columns: x => new { x.PartitionId, x.TargetStayId },
                    principalTable: "VehicleStays",
                    principalColumns: new[] { "PartitionId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StayObservationAssignments",
            columns: table => new
            {
                EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkshopId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PlateNormalized = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                StayId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GroupOriginEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MatchedMovementEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Role = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StayObservationAssignments", x => x.EventId);
                table.CheckConstraint("CK_StayAssignment_Group", "([GroupId] IS NULL AND [GroupOriginEventId] IS NULL AND [StayId] IS NULL) OR ([GroupId] IS NOT NULL AND [GroupOriginEventId] IS NOT NULL)");
                table.CheckConstraint("CK_StayAssignment_Reason", "LEN(LTRIM(RTRIM([Reason]))) > 0");
                table.CheckConstraint("CK_StayAssignment_Role", "([StayId] IS NULL AND [Role] = 'Unassociated') OR ([StayId] IS NOT NULL AND [Role] IN ('Entry','Exit','Intermediate','Repeated'))");
                table.ForeignKey(
                    name: "FK_StayObservationAssignments_LprEvents_EventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.EventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayObservationAssignments_LprEvents_GroupOriginEventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.GroupOriginEventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayObservationAssignments_LprEvents_MatchedMovementEventId_WorkshopId_PlateNormalized",
                    columns: x => new { x.MatchedMovementEventId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "LprEvents",
                    principalColumns: new[] { "EventId", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayObservationAssignments_StayProjectionRevisions_PartitionId_Revision",
                    columns: x => new { x.PartitionId, x.Revision },
                    principalTable: "StayProjectionRevisions",
                    principalColumns: new[] { "PartitionId", "Revision" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayObservationAssignments_VehicleStayPartitions_PartitionId_WorkshopId_PlateNormalized",
                    columns: x => new { x.PartitionId, x.WorkshopId, x.PlateNormalized },
                    principalTable: "VehicleStayPartitions",
                    principalColumns: new[] { "Id", "WorkshopId", "PlateNormalized" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_StayObservationAssignments_VehicleStays_PartitionId_StayId",
                    columns: x => new { x.PartitionId, x.StayId },
                    principalTable: "VehicleStays",
                    principalColumns: new[] { "PartitionId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LprEvents_WorkshopId_PlateNormalized_CapturedAt_EventOrderKey",
            table: "LprEvents",
            columns: new[] { "WorkshopId", "PlateNormalized", "CapturedAt", "EventOrderKey" });

        migrationBuilder.CreateIndex(
            name: "IX_LprDevices_CurrentLocationId_Id_WorkshopId",
            table: "LprDevices",
            columns: new[] { "CurrentLocationId", "Id", "WorkshopId" });

        migrationBuilder.AddCheckConstraint(
            name: "CK_DeviceAudit_Location",
            table: "DeviceAdministrationAudits",
            sql: "([CurrentLocationId] IS NULL AND [CurrentLocationDescription] IS NULL AND [CurrentLocationRecordedAt] IS NULL OR [CurrentLocationId] IS NOT NULL AND [CurrentLocationDescription] IS NOT NULL AND LEN(LTRIM(RTRIM([CurrentLocationDescription]))) > 0 AND [CurrentLocationRecordedAt] IS NOT NULL AND DATEPART(TZOFFSET,[CurrentLocationRecordedAt]) = 0) AND ([PreviousLocationId] IS NULL AND [PreviousLocationDescription] IS NULL AND [PreviousLocationRecordedAt] IS NULL OR [PreviousLocationId] IS NOT NULL AND [PreviousLocationDescription] IS NOT NULL AND LEN(LTRIM(RTRIM([PreviousLocationDescription]))) > 0 AND [PreviousLocationRecordedAt] IS NOT NULL AND DATEPART(TZOFFSET,[PreviousLocationRecordedAt]) = 0)");

        migrationBuilder.AddCheckConstraint(
            name: "CK_DeviceAudit_LocationChanged",
            table: "DeviceAdministrationAudits",
            sql: "[Operation] <> 'LocationChanged' OR ([CurrentLocationId] IS NOT NULL AND ([PreviousLocationId] IS NULL OR [CurrentLocationId] <> [PreviousLocationId]) AND [CurrentAuthorizationVersion] = [PreviousAuthorizationVersion] AND [CurrentName] = [PreviousName] AND [CurrentIsActive] = [PreviousIsActive] AND [CurrentHasCredentials] = [PreviousHasCredentials])");

        migrationBuilder.AddCheckConstraint(
            name: "CK_DeviceAudit_Operation",
            table: "DeviceAdministrationAudits",
            sql: "[Operation] IN ('Created','Renamed','Deactivated','Reactivated','CredentialsRotated','CredentialsRevoked','LocationChanged')");

        migrationBuilder.CreateIndex(
            name: "IX_LprDeviceLocations_DeviceId_CreationOrder",
            table: "LprDeviceLocations",
            columns: new[] { "DeviceId", "CreationOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LprDeviceLocations_DeviceId_RecordedAt_Id",
            table: "LprDeviceLocations",
            columns: new[] { "DeviceId", "RecordedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_LprDeviceLocations_DeviceId_WorkshopId",
            table: "LprDeviceLocations",
            columns: new[] { "DeviceId", "WorkshopId" });

        migrationBuilder.CreateIndex(
            name: "IX_LprObservationLocations_EventId_DeviceId_WorkshopId",
            table: "LprObservationLocations",
            columns: new[] { "EventId", "DeviceId", "WorkshopId" });

        migrationBuilder.CreateIndex(
            name: "IX_LprObservationLocations_LocationId_DeviceId_WorkshopId",
            table: "LprObservationLocations",
            columns: new[] { "LocationId", "DeviceId", "WorkshopId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayConfiguration_ActivePolicyId",
            table: "StayConfiguration",
            column: "ActivePolicyId");

        migrationBuilder.CreateIndex(
            name: "IX_StayDayClosures_NextCheckAt_LocalDate_PartitionId",
            table: "StayDayClosures",
            columns: new[] { "NextCheckAt", "LocalDate", "PartitionId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayLineage_PartitionId_SourceStayId",
            table: "StayLineage",
            columns: new[] { "PartitionId", "SourceStayId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayLineage_PartitionId_TargetStayId",
            table: "StayLineage",
            columns: new[] { "PartitionId", "TargetStayId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_EventId_WorkshopId_PlateNormalized",
            table: "StayObservationAssignments",
            columns: new[] { "EventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_GroupOriginEventId_WorkshopId_PlateNormalized",
            table: "StayObservationAssignments",
            columns: new[] { "GroupOriginEventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_MatchedMovementEventId_WorkshopId_PlateNormalized",
            table: "StayObservationAssignments",
            columns: new[] { "MatchedMovementEventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_PartitionId_EventId",
            table: "StayObservationAssignments",
            columns: new[] { "PartitionId", "EventId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_PartitionId_GroupId",
            table: "StayObservationAssignments",
            columns: new[] { "PartitionId", "GroupId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_PartitionId_Revision",
            table: "StayObservationAssignments",
            columns: new[] { "PartitionId", "Revision" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_PartitionId_StayId",
            table: "StayObservationAssignments",
            columns: new[] { "PartitionId", "StayId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_PartitionId_WorkshopId_PlateNormalized",
            table: "StayObservationAssignments",
            columns: new[] { "PartitionId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_StayObservationAssignments_StayId_EventId",
            table: "StayObservationAssignments",
            columns: new[] { "StayId", "EventId" });

        migrationBuilder.CreateIndex(
            name: "IX_StayPolicies_Fingerprint",
            table: "StayPolicies",
            column: "Fingerprint",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_StayProjectionRevisions_CausingEventId_WorkshopId_PlateNormalized",
            table: "StayProjectionRevisions",
            columns: new[] { "CausingEventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_StayProjectionRevisions_PartitionId_WorkshopId_PlateNormalized",
            table: "StayProjectionRevisions",
            columns: new[] { "PartitionId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStayPartitions_NextClosureAt_Id",
            table: "VehicleStayPartitions",
            columns: new[] { "NextClosureAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStayPartitions_WorkshopId",
            table: "VehicleStayPartitions",
            column: "WorkshopId");

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStayPartitions_WorkshopIdentityKey_PlateIdentityKey",
            table: "VehicleStayPartitions",
            columns: new[] { "WorkshopIdentityKey", "PlateIdentityKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_EntryEventId_WorkshopId_PlateNormalized",
            table: "VehicleStays",
            columns: new[] { "EntryEventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_ExitEventId_WorkshopId_PlateNormalized",
            table: "VehicleStays",
            columns: new[] { "ExitEventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_OriginEventId",
            table: "VehicleStays",
            column: "OriginEventId",
            unique: true,
            filter: "[AbsorbedByStayId] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_OriginEventId_WorkshopId_PlateNormalized",
            table: "VehicleStays",
            columns: new[] { "OriginEventId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PartitionId_AbsorbedByStayId",
            table: "VehicleStays",
            columns: new[] { "PartitionId", "AbsorbedByStayId" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PartitionId_CreationOrder",
            table: "VehicleStays",
            columns: new[] { "PartitionId", "CreationOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PartitionId_EntryAt_Id",
            table: "VehicleStays",
            columns: new[] { "PartitionId", "EntryAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PartitionId_ExitAt_Id",
            table: "VehicleStays",
            columns: new[] { "PartitionId", "ExitAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PartitionId_Revision",
            table: "VehicleStays",
            columns: new[] { "PartitionId", "Revision" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PartitionId_WorkshopId_PlateNormalized",
            table: "VehicleStays",
            columns: new[] { "PartitionId", "WorkshopId", "PlateNormalized" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_PolicyId",
            table: "VehicleStays",
            column: "PolicyId");

        migrationBuilder.CreateIndex(
            name: "IX_VehicleStays_State_NextClosureAt_Id",
            table: "VehicleStays",
            columns: new[] { "State", "NextClosureAt", "Id" });

        migrationBuilder.AddForeignKey(
            name: "FK_LprDevices_LprDeviceLocations_CurrentLocationId_Id_WorkshopId",
            table: "LprDevices",
            columns: new[] { "CurrentLocationId", "Id", "WorkshopId" },
            principalTable: "LprDeviceLocations",
            principalColumns: new[] { "Id", "DeviceId", "WorkshopId" },
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Once RF-004 data exists, recovery requires a reviewed preservation/restore procedure.
        // Refuse an automatic downgrade before it can erase audit, location or projection history.
        migrationBuilder.Sql("""
            IF EXISTS (SELECT 1 FROM [StayPolicies])
               OR EXISTS (SELECT 1 FROM [StayConfiguration])
               OR EXISTS (SELECT 1 FROM [VehicleStayPartitions])
               OR EXISTS (SELECT 1 FROM [VehicleStays])
               OR EXISTS (SELECT 1 FROM [StayProjectionRevisions])
               OR EXISTS (SELECT 1 FROM [StayLineage])
               OR EXISTS (SELECT 1 FROM [StayObservationAssignments])
               OR EXISTS (SELECT 1 FROM [StayDayClosures])
               OR EXISTS (SELECT 1 FROM [LprDeviceLocations])
               OR EXISTS (SELECT 1 FROM [LprObservationLocations])
               OR EXISTS (SELECT 1 FROM [LprDevices] WHERE [CurrentLocationId] IS NOT NULL)
               OR EXISTS (SELECT 1 FROM [DeviceAdministrationAudits]
                          WHERE [Operation] = 'LocationChanged' OR [CurrentLocationId] IS NOT NULL
                             OR [PreviousLocationId] IS NOT NULL)
            BEGIN
                THROW 51000, 'RF-004 downgrade requires a reviewed data preservation procedure.', 1;
            END;
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_LprDevices_LprDeviceLocations_CurrentLocationId_Id_WorkshopId",
            table: "LprDevices");

        migrationBuilder.DropTable(
            name: "LprObservationLocations");

        migrationBuilder.DropTable(
            name: "StayConfiguration");

        migrationBuilder.DropTable(
            name: "StayDayClosures");

        migrationBuilder.DropTable(
            name: "StayLineage");

        migrationBuilder.DropTable(
            name: "StayObservationAssignments");

        migrationBuilder.DropTable(
            name: "LprDeviceLocations");

        migrationBuilder.DropTable(
            name: "VehicleStays");

        migrationBuilder.DropTable(
            name: "StayPolicies");

        migrationBuilder.DropTable(
            name: "StayProjectionRevisions");

        migrationBuilder.DropTable(
            name: "VehicleStayPartitions");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_LprEvents_EventId_DeviceId_WorkshopId",
            table: "LprEvents");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_LprEvents_EventId_WorkshopId_PlateNormalized",
            table: "LprEvents");

        migrationBuilder.DropIndex(
            name: "IX_LprEvents_WorkshopId_PlateNormalized_CapturedAt_EventOrderKey",
            table: "LprEvents");

        migrationBuilder.DropIndex(
            name: "IX_LprDevices_CurrentLocationId_Id_WorkshopId",
            table: "LprDevices");

        migrationBuilder.DropCheckConstraint(
            name: "CK_DeviceAudit_Location",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropCheckConstraint(
            name: "CK_DeviceAudit_LocationChanged",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropCheckConstraint(
            name: "CK_DeviceAudit_Operation",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropColumn(
            name: "EventOrderKey",
            table: "LprEvents");

        migrationBuilder.DropColumn(
            name: "CurrentLocationId",
            table: "LprDevices");

        migrationBuilder.DropColumn(
            name: "CurrentLocationDescription",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropColumn(
            name: "CurrentLocationId",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropColumn(
            name: "CurrentLocationRecordedAt",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropColumn(
            name: "PreviousLocationDescription",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropColumn(
            name: "PreviousLocationId",
            table: "DeviceAdministrationAudits");

        migrationBuilder.DropColumn(
            name: "PreviousLocationRecordedAt",
            table: "DeviceAdministrationAudits");

        migrationBuilder.AddCheckConstraint(
            name: "CK_DeviceAudit_Operation",
            table: "DeviceAdministrationAudits",
            sql: "[Operation] IN ('Created','Renamed','Deactivated','Reactivated','CredentialsRotated','CredentialsRevoked')");
    }
}
