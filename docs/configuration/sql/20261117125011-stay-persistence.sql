BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] DROP CONSTRAINT [CK_DeviceAudit_Operation];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [LprDevices] ADD [CurrentLocationId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] ADD [CurrentLocationDescription] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] ADD [CurrentLocationId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] ADD [CurrentLocationRecordedAt] datetimeoffset(7) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] ADD [PreviousLocationDescription] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] ADD [PreviousLocationId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [DeviceAdministrationAudits] ADD [PreviousLocationRecordedAt] datetimeoffset(7) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    EXEC(N'ALTER TABLE [LprEvents] ADD [EventOrderKey] AS CONVERT(binary(16), REPLACE(CONVERT(char(36), [EventId]), ''-'', ''''), 2) PERSISTED');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [LprEvents] ADD CONSTRAINT [AK_LprEvents_EventId_DeviceId_WorkshopId] UNIQUE ([EventId], [DeviceId], [WorkshopId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [LprEvents] ADD CONSTRAINT [AK_LprEvents_EventId_WorkshopId_PlateNormalized] UNIQUE ([EventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [LprDeviceLocations] (
        [Id] uniqueidentifier NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [WorkshopId] nvarchar(256) NOT NULL,
        [Description] nvarchar(256) NOT NULL,
        [RecordedAt] datetimeoffset(7) NOT NULL,
        [CreationOrder] bigint NOT NULL,
        CONSTRAINT [PK_LprDeviceLocations] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_LprDeviceLocations_Id_DeviceId_WorkshopId] UNIQUE ([Id], [DeviceId], [WorkshopId]),
        CONSTRAINT [CK_DeviceLocation_Description] CHECK (LEN(LTRIM(RTRIM([Description]))) > 0),
        CONSTRAINT [CK_DeviceLocation_Identity] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [CreationOrder] > 0),
        CONSTRAINT [CK_DeviceLocation_Utc] CHECK (DATEPART(TZOFFSET,[RecordedAt]) = 0),
        CONSTRAINT [FK_LprDeviceLocations_LprDevices_DeviceId_WorkshopId] FOREIGN KEY ([DeviceId], [WorkshopId]) REFERENCES [LprDevices] ([Id], [WorkshopId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [StayPolicies] (
        [Id] uniqueidentifier NOT NULL,
        [Fingerprint] varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [EstimatedDurationTicks] bigint NOT NULL,
        [RepeatWindowTicks] bigint NOT NULL,
        [MaximumAssociationTicks] bigint NOT NULL,
        [TimeZoneId] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [AlgorithmVersion] int NOT NULL,
        CONSTRAINT [PK_StayPolicies] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_StayPolicy_Identity] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND LEN([Fingerprint]) = 64),
        CONSTRAINT [CK_StayPolicy_Values] CHECK ([EstimatedDurationTicks] > 0 AND [RepeatWindowTicks] > 0 AND [MaximumAssociationTicks] > 0 AND [AlgorithmVersion] > 0),
        CONSTRAINT [CK_StayPolicy_Zone] CHECK ([TimeZoneId] = N'Atlantic/Canary')
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [VehicleStayPartitions] (
        [Id] uniqueidentifier NOT NULL,
        [WorkshopId] nvarchar(256) NOT NULL,
        [PlateNormalized] nvarchar(32) NOT NULL,
        [InputRevision] bigint NOT NULL,
        [ProjectionRevision] bigint NOT NULL,
        [LastCreationOrder] bigint NOT NULL,
        [NextClosureAt] datetimeoffset(7) NULL,
        [PlateIdentityKey] AS CONVERT(varbinary(64), [PlateNormalized]) PERSISTED,
        [WorkshopIdentityKey] AS CONVERT(varbinary(512), [WorkshopId]) PERSISTED,
        CONSTRAINT [PK_VehicleStayPartitions] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_VehicleStayPartitions_Id_WorkshopId_PlateNormalized] UNIQUE ([Id], [WorkshopId], [PlateNormalized]),
        CONSTRAINT [CK_StayPartition_Identity] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND LEN(LTRIM(RTRIM([WorkshopId]))) > 0 AND LEN(LTRIM(RTRIM([PlateNormalized]))) > 0),
        CONSTRAINT [CK_StayPartition_Revisions] CHECK ([InputRevision] >= 0 AND [ProjectionRevision] >= 0 AND [LastCreationOrder] >= 0),
        CONSTRAINT [CK_StayPartition_Utc] CHECK ([NextClosureAt] IS NULL OR DATEPART(TZOFFSET,[NextClosureAt]) = 0),
        CONSTRAINT [FK_VehicleStayPartitions_WorkshopAccess_WorkshopId] FOREIGN KEY ([WorkshopId]) REFERENCES [WorkshopAccess] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [LprObservationLocations] (
        [EventId] uniqueidentifier NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [WorkshopId] nvarchar(256) NOT NULL,
        [LocationId] uniqueidentifier NULL,
        [Provenance] varchar(32) NOT NULL,
        CONSTRAINT [PK_LprObservationLocations] PRIMARY KEY ([EventId]),
        CONSTRAINT [CK_ObservationLocation_Provenance] CHECK (([LocationId] IS NULL AND [Provenance] = 'Unknown') OR ([LocationId] IS NOT NULL AND [Provenance] IN ('AtCapture','LatestKnownForFutureCapture'))),
        CONSTRAINT [FK_LprObservationLocations_LprDeviceLocations_LocationId_DeviceId_WorkshopId] FOREIGN KEY ([LocationId], [DeviceId], [WorkshopId]) REFERENCES [LprDeviceLocations] ([Id], [DeviceId], [WorkshopId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_LprObservationLocations_LprEvents_EventId_DeviceId_WorkshopId] FOREIGN KEY ([EventId], [DeviceId], [WorkshopId]) REFERENCES [LprEvents] ([EventId], [DeviceId], [WorkshopId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [StayConfiguration] (
        [Id] int NOT NULL,
        [ActivePolicyId] uniqueidentifier NOT NULL,
        [Version] uniqueidentifier NOT NULL,
        [ActivatedAt] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_StayConfiguration] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_StayConfiguration_Singleton] CHECK ([Id] = 1),
        CONSTRAINT [CK_StayConfiguration_Utc] CHECK (DATEPART(TZOFFSET,[ActivatedAt]) = 0),
        CONSTRAINT [CK_StayConfiguration_Version] CHECK ([Version] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [FK_StayConfiguration_StayPolicies_ActivePolicyId] FOREIGN KEY ([ActivePolicyId]) REFERENCES [StayPolicies] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [StayDayClosures] (
        [PartitionId] uniqueidentifier NOT NULL,
        [LocalDate] date NOT NULL,
        [ProcessedInputRevision] bigint NOT NULL,
        [ClosedAt] datetimeoffset(7) NOT NULL,
        [NextCheckAt] datetimeoffset(7) NULL,
        CONSTRAINT [PK_StayDayClosures] PRIMARY KEY ([PartitionId], [LocalDate]),
        CONSTRAINT [CK_StayClosure_Revision] CHECK ([ProcessedInputRevision] >= 0),
        CONSTRAINT [CK_StayClosure_Utc] CHECK (DATEPART(TZOFFSET,[ClosedAt]) = 0 AND ([NextCheckAt] IS NULL OR DATEPART(TZOFFSET,[NextCheckAt]) = 0)),
        CONSTRAINT [FK_StayDayClosures_VehicleStayPartitions_PartitionId] FOREIGN KEY ([PartitionId]) REFERENCES [VehicleStayPartitions] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [StayProjectionRevisions] (
        [PartitionId] uniqueidentifier NOT NULL,
        [Revision] bigint NOT NULL,
        [WorkshopId] nvarchar(256) NOT NULL,
        [PlateNormalized] nvarchar(32) NOT NULL,
        [InputRevision] bigint NOT NULL,
        [Cause] varchar(24) NOT NULL,
        [EvaluatedAt] datetimeoffset(7) NOT NULL,
        [CausingEventId] uniqueidentifier NULL,
        [ChangesJson] nvarchar(max) NOT NULL,
        [FormatVersion] int NOT NULL,
        CONSTRAINT [PK_StayProjectionRevisions] PRIMARY KEY ([PartitionId], [Revision]),
        CONSTRAINT [CK_StayRevision_Cause] CHECK (([Cause] = 'Ingestion' AND [CausingEventId] IS NOT NULL) OR ([Cause] IN ('DailyClosure','InitialProjection') AND [CausingEventId] IS NULL)),
        CONSTRAINT [CK_StayRevision_Utc] CHECK (DATEPART(TZOFFSET,[EvaluatedAt]) = 0),
        CONSTRAINT [CK_StayRevision_Values] CHECK ([Revision] > 0 AND [InputRevision] >= 0 AND [FormatVersion] > 0 AND ISJSON([ChangesJson]) = 1),
        CONSTRAINT [FK_StayProjectionRevisions_LprEvents_CausingEventId_WorkshopId_PlateNormalized] FOREIGN KEY ([CausingEventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayProjectionRevisions_VehicleStayPartitions_PartitionId_WorkshopId_PlateNormalized] FOREIGN KEY ([PartitionId], [WorkshopId], [PlateNormalized]) REFERENCES [VehicleStayPartitions] ([Id], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [VehicleStays] (
        [Id] uniqueidentifier NOT NULL,
        [PartitionId] uniqueidentifier NOT NULL,
        [WorkshopId] nvarchar(256) NOT NULL,
        [PlateNormalized] nvarchar(32) NOT NULL,
        [PolicyId] uniqueidentifier NOT NULL,
        [OriginEventId] uniqueidentifier NOT NULL,
        [CreatedAt] datetimeoffset(7) NOT NULL,
        [CreationOrder] bigint NOT NULL,
        [UpdatedAt] datetimeoffset(7) NOT NULL,
        [State] varchar(24) NOT NULL,
        [EntryAt] datetimeoffset(7) NULL,
        [EntrySource] varchar(16) NULL,
        [EntryEventId] uniqueidentifier NULL,
        [ExitAt] datetimeoffset(7) NULL,
        [ExitSource] varchar(16) NULL,
        [ExitEventId] uniqueidentifier NULL,
        [Revision] bigint NOT NULL,
        [AbsorbedByStayId] uniqueidentifier NULL,
        [NextClosureAt] datetimeoffset(7) NULL,
        CONSTRAINT [PK_VehicleStays] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_VehicleStays_PartitionId_Id] UNIQUE ([PartitionId], [Id]),
        CONSTRAINT [CK_Stay_Chronology] CHECK ([UpdatedAt] >= [CreatedAt] AND ([EntryAt] IS NULL OR [ExitAt] IS NULL OR [ExitAt] >= [EntryAt])),
        CONSTRAINT [CK_Stay_Entry] CHECK (([EntryAt] IS NULL AND [EntrySource] IS NULL AND [EntryEventId] IS NULL) OR ([EntryAt] IS NOT NULL AND [EntrySource] IS NOT NULL AND (([EntrySource] = 'Estimated' AND [EntryEventId] IS NULL) OR ([EntrySource] IN ('Explicit','Inferred') AND [EntryEventId] IS NOT NULL)))),
        CONSTRAINT [CK_Stay_Estimation] CHECK (([EntrySource] IS NULL OR [EntrySource] <> 'Estimated' OR ([ExitEventId] IS NOT NULL AND [ExitSource] IN ('Explicit','Inferred'))) AND ([ExitSource] IS NULL OR [ExitSource] <> 'Estimated' OR ([EntryEventId] IS NOT NULL AND [EntrySource] IN ('Explicit','Inferred')))),
        CONSTRAINT [CK_Stay_Exit] CHECK (([ExitAt] IS NULL AND [ExitSource] IS NULL AND [ExitEventId] IS NULL) OR ([ExitAt] IS NOT NULL AND [ExitSource] IS NOT NULL AND (([ExitSource] = 'Estimated' AND [ExitEventId] IS NULL) OR ([ExitSource] IN ('Explicit','Inferred') AND [ExitEventId] IS NOT NULL)))),
        CONSTRAINT [CK_Stay_Identity] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [CreationOrder] > 0 AND [Revision] > 0 AND ([AbsorbedByStayId] IS NULL OR [AbsorbedByStayId] <> [Id])),
        CONSTRAINT [CK_Stay_State] CHECK (([State] = 'Open' AND [EntryAt] IS NOT NULL AND [ExitAt] IS NULL) OR ([State] = 'PendingEntry' AND [EntryAt] IS NULL AND [ExitAt] IS NOT NULL) OR ([State] = 'ClosedProvisional' AND [EntryAt] IS NOT NULL AND [ExitAt] IS NOT NULL)),
        CONSTRAINT [CK_Stay_Utc] CHECK (DATEPART(TZOFFSET,[CreatedAt]) = 0 AND DATEPART(TZOFFSET,[UpdatedAt]) = 0 AND ([EntryAt] IS NULL OR DATEPART(TZOFFSET,[EntryAt]) = 0) AND ([ExitAt] IS NULL OR DATEPART(TZOFFSET,[ExitAt]) = 0) AND ([NextClosureAt] IS NULL OR DATEPART(TZOFFSET,[NextClosureAt]) = 0)),
        CONSTRAINT [FK_VehicleStays_LprEvents_EntryEventId_WorkshopId_PlateNormalized] FOREIGN KEY ([EntryEventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VehicleStays_LprEvents_ExitEventId_WorkshopId_PlateNormalized] FOREIGN KEY ([ExitEventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VehicleStays_LprEvents_OriginEventId_WorkshopId_PlateNormalized] FOREIGN KEY ([OriginEventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VehicleStays_StayPolicies_PolicyId] FOREIGN KEY ([PolicyId]) REFERENCES [StayPolicies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VehicleStays_StayProjectionRevisions_PartitionId_Revision] FOREIGN KEY ([PartitionId], [Revision]) REFERENCES [StayProjectionRevisions] ([PartitionId], [Revision]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VehicleStays_VehicleStayPartitions_PartitionId_WorkshopId_PlateNormalized] FOREIGN KEY ([PartitionId], [WorkshopId], [PlateNormalized]) REFERENCES [VehicleStayPartitions] ([Id], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VehicleStays_VehicleStays_PartitionId_AbsorbedByStayId] FOREIGN KEY ([PartitionId], [AbsorbedByStayId]) REFERENCES [VehicleStays] ([PartitionId], [Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [StayLineage] (
        [PartitionId] uniqueidentifier NOT NULL,
        [Revision] bigint NOT NULL,
        [SourceStayId] uniqueidentifier NOT NULL,
        [TargetStayId] uniqueidentifier NOT NULL,
        [Kind] varchar(8) NOT NULL,
        [RecordedAt] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_StayLineage] PRIMARY KEY ([PartitionId], [Revision], [Kind], [SourceStayId], [TargetStayId]),
        CONSTRAINT [CK_StayLineage_Kind] CHECK ([Kind] IN ('Fusion','Split') AND [SourceStayId] <> [TargetStayId]),
        CONSTRAINT [CK_StayLineage_Utc] CHECK (DATEPART(TZOFFSET,[RecordedAt]) = 0),
        CONSTRAINT [FK_StayLineage_StayProjectionRevisions_PartitionId_Revision] FOREIGN KEY ([PartitionId], [Revision]) REFERENCES [StayProjectionRevisions] ([PartitionId], [Revision]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayLineage_VehicleStays_PartitionId_SourceStayId] FOREIGN KEY ([PartitionId], [SourceStayId]) REFERENCES [VehicleStays] ([PartitionId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayLineage_VehicleStays_PartitionId_TargetStayId] FOREIGN KEY ([PartitionId], [TargetStayId]) REFERENCES [VehicleStays] ([PartitionId], [Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE TABLE [StayObservationAssignments] (
        [EventId] uniqueidentifier NOT NULL,
        [PartitionId] uniqueidentifier NOT NULL,
        [WorkshopId] nvarchar(256) NOT NULL,
        [PlateNormalized] nvarchar(32) NOT NULL,
        [StayId] uniqueidentifier NULL,
        [GroupId] uniqueidentifier NULL,
        [GroupOriginEventId] uniqueidentifier NULL,
        [MatchedMovementEventId] uniqueidentifier NULL,
        [Role] varchar(16) NOT NULL,
        [Reason] varchar(64) NOT NULL,
        [Revision] bigint NOT NULL,
        CONSTRAINT [PK_StayObservationAssignments] PRIMARY KEY ([EventId]),
        CONSTRAINT [CK_StayAssignment_Group] CHECK (([GroupId] IS NULL AND [GroupOriginEventId] IS NULL AND [StayId] IS NULL) OR ([GroupId] IS NOT NULL AND [GroupOriginEventId] IS NOT NULL)),
        CONSTRAINT [CK_StayAssignment_Reason] CHECK (LEN(LTRIM(RTRIM([Reason]))) > 0),
        CONSTRAINT [CK_StayAssignment_Role] CHECK (([StayId] IS NULL AND [Role] = 'Unassociated') OR ([StayId] IS NOT NULL AND [Role] IN ('Entry','Exit','Intermediate','Repeated'))),
        CONSTRAINT [FK_StayObservationAssignments_LprEvents_EventId_WorkshopId_PlateNormalized] FOREIGN KEY ([EventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayObservationAssignments_LprEvents_GroupOriginEventId_WorkshopId_PlateNormalized] FOREIGN KEY ([GroupOriginEventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayObservationAssignments_LprEvents_MatchedMovementEventId_WorkshopId_PlateNormalized] FOREIGN KEY ([MatchedMovementEventId], [WorkshopId], [PlateNormalized]) REFERENCES [LprEvents] ([EventId], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayObservationAssignments_StayProjectionRevisions_PartitionId_Revision] FOREIGN KEY ([PartitionId], [Revision]) REFERENCES [StayProjectionRevisions] ([PartitionId], [Revision]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayObservationAssignments_VehicleStayPartitions_PartitionId_WorkshopId_PlateNormalized] FOREIGN KEY ([PartitionId], [WorkshopId], [PlateNormalized]) REFERENCES [VehicleStayPartitions] ([Id], [WorkshopId], [PlateNormalized]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StayObservationAssignments_VehicleStays_PartitionId_StayId] FOREIGN KEY ([PartitionId], [StayId]) REFERENCES [VehicleStays] ([PartitionId], [Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_LprEvents_WorkshopId_PlateNormalized_CapturedAt_EventOrderKey] ON [LprEvents] ([WorkshopId], [PlateNormalized], [CapturedAt], [EventOrderKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_LprDevices_CurrentLocationId_Id_WorkshopId] ON [LprDevices] ([CurrentLocationId], [Id], [WorkshopId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    EXEC(N'ALTER TABLE [DeviceAdministrationAudits] ADD CONSTRAINT [CK_DeviceAudit_Location] CHECK (([CurrentLocationId] IS NULL AND [CurrentLocationDescription] IS NULL AND [CurrentLocationRecordedAt] IS NULL OR [CurrentLocationId] IS NOT NULL AND [CurrentLocationDescription] IS NOT NULL AND LEN(LTRIM(RTRIM([CurrentLocationDescription]))) > 0 AND [CurrentLocationRecordedAt] IS NOT NULL AND DATEPART(TZOFFSET,[CurrentLocationRecordedAt]) = 0) AND ([PreviousLocationId] IS NULL AND [PreviousLocationDescription] IS NULL AND [PreviousLocationRecordedAt] IS NULL OR [PreviousLocationId] IS NOT NULL AND [PreviousLocationDescription] IS NOT NULL AND LEN(LTRIM(RTRIM([PreviousLocationDescription]))) > 0 AND [PreviousLocationRecordedAt] IS NOT NULL AND DATEPART(TZOFFSET,[PreviousLocationRecordedAt]) = 0))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    EXEC(N'ALTER TABLE [DeviceAdministrationAudits] ADD CONSTRAINT [CK_DeviceAudit_LocationChanged] CHECK ([Operation] <> ''LocationChanged'' OR ([CurrentLocationId] IS NOT NULL AND ([PreviousLocationId] IS NULL OR [CurrentLocationId] <> [PreviousLocationId]) AND [CurrentAuthorizationVersion] = [PreviousAuthorizationVersion] AND [CurrentName] = [PreviousName] AND [CurrentIsActive] = [PreviousIsActive] AND [CurrentHasCredentials] = [PreviousHasCredentials]))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    EXEC(N'ALTER TABLE [DeviceAdministrationAudits] ADD CONSTRAINT [CK_DeviceAudit_Operation] CHECK ([Operation] IN (''Created'',''Renamed'',''Deactivated'',''Reactivated'',''CredentialsRotated'',''CredentialsRevoked'',''LocationChanged''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE UNIQUE INDEX [IX_LprDeviceLocations_DeviceId_CreationOrder] ON [LprDeviceLocations] ([DeviceId], [CreationOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_LprDeviceLocations_DeviceId_RecordedAt_Id] ON [LprDeviceLocations] ([DeviceId], [RecordedAt], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_LprDeviceLocations_DeviceId_WorkshopId] ON [LprDeviceLocations] ([DeviceId], [WorkshopId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_LprObservationLocations_EventId_DeviceId_WorkshopId] ON [LprObservationLocations] ([EventId], [DeviceId], [WorkshopId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_LprObservationLocations_LocationId_DeviceId_WorkshopId] ON [LprObservationLocations] ([LocationId], [DeviceId], [WorkshopId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayConfiguration_ActivePolicyId] ON [StayConfiguration] ([ActivePolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayDayClosures_NextCheckAt_LocalDate_PartitionId] ON [StayDayClosures] ([NextCheckAt], [LocalDate], [PartitionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayLineage_PartitionId_SourceStayId] ON [StayLineage] ([PartitionId], [SourceStayId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayLineage_PartitionId_TargetStayId] ON [StayLineage] ([PartitionId], [TargetStayId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_EventId_WorkshopId_PlateNormalized] ON [StayObservationAssignments] ([EventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_GroupOriginEventId_WorkshopId_PlateNormalized] ON [StayObservationAssignments] ([GroupOriginEventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_MatchedMovementEventId_WorkshopId_PlateNormalized] ON [StayObservationAssignments] ([MatchedMovementEventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_PartitionId_EventId] ON [StayObservationAssignments] ([PartitionId], [EventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_PartitionId_GroupId] ON [StayObservationAssignments] ([PartitionId], [GroupId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_PartitionId_Revision] ON [StayObservationAssignments] ([PartitionId], [Revision]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_PartitionId_StayId] ON [StayObservationAssignments] ([PartitionId], [StayId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_PartitionId_WorkshopId_PlateNormalized] ON [StayObservationAssignments] ([PartitionId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayObservationAssignments_StayId_EventId] ON [StayObservationAssignments] ([StayId], [EventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE UNIQUE INDEX [IX_StayPolicies_Fingerprint] ON [StayPolicies] ([Fingerprint]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayProjectionRevisions_CausingEventId_WorkshopId_PlateNormalized] ON [StayProjectionRevisions] ([CausingEventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_StayProjectionRevisions_PartitionId_WorkshopId_PlateNormalized] ON [StayProjectionRevisions] ([PartitionId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStayPartitions_NextClosureAt_Id] ON [VehicleStayPartitions] ([NextClosureAt], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStayPartitions_WorkshopId] ON [VehicleStayPartitions] ([WorkshopId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE UNIQUE INDEX [IX_VehicleStayPartitions_WorkshopIdentityKey_PlateIdentityKey] ON [VehicleStayPartitions] ([WorkshopIdentityKey], [PlateIdentityKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_EntryEventId_WorkshopId_PlateNormalized] ON [VehicleStays] ([EntryEventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_ExitEventId_WorkshopId_PlateNormalized] ON [VehicleStays] ([ExitEventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_VehicleStays_OriginEventId] ON [VehicleStays] ([OriginEventId]) WHERE [AbsorbedByStayId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_OriginEventId_WorkshopId_PlateNormalized] ON [VehicleStays] ([OriginEventId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_PartitionId_AbsorbedByStayId] ON [VehicleStays] ([PartitionId], [AbsorbedByStayId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE UNIQUE INDEX [IX_VehicleStays_PartitionId_CreationOrder] ON [VehicleStays] ([PartitionId], [CreationOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_PartitionId_EntryAt_Id] ON [VehicleStays] ([PartitionId], [EntryAt], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_PartitionId_ExitAt_Id] ON [VehicleStays] ([PartitionId], [ExitAt], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_PartitionId_Revision] ON [VehicleStays] ([PartitionId], [Revision]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_PartitionId_WorkshopId_PlateNormalized] ON [VehicleStays] ([PartitionId], [WorkshopId], [PlateNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_PolicyId] ON [VehicleStays] ([PolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    CREATE INDEX [IX_VehicleStays_State_NextClosureAt_Id] ON [VehicleStays] ([State], [NextClosureAt], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    ALTER TABLE [LprDevices] ADD CONSTRAINT [FK_LprDevices_LprDeviceLocations_CurrentLocationId_Id_WorkshopId] FOREIGN KEY ([CurrentLocationId], [Id], [WorkshopId]) REFERENCES [LprDeviceLocations] ([Id], [DeviceId], [WorkshopId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117125011_StayPersistence'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117125011_StayPersistence', N'10.0.12');
END;

COMMIT;
GO

