BEGIN TRANSACTION;
ALTER TABLE [VehicleStays] ADD [StayOrderKey] AS CONVERT(binary(16), REPLACE(CONVERT(char(36), [Id]), '-', ''), 2) PERSISTED;

CREATE TABLE [HistoryAccessAudits] (
    [Id] uniqueidentifier NOT NULL,
    [ActorUserId] uniqueidentifier NOT NULL,
    [OccurredAt] datetimeoffset(7) NOT NULL,
    [Operation] varchar(64) NOT NULL,
    [ScopeKind] varchar(16) NOT NULL,
    [Status] varchar(24) NOT NULL,
    [CorrelationId] nvarchar(128) NOT NULL,
    [RowCount] bigint NOT NULL,
    CONSTRAINT [PK_HistoryAccessAudits] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_HistoryAudit_Rows] CHECK ([RowCount] >= 0),
    CONSTRAINT [CK_HistoryAudit_Scope] CHECK ([ScopeKind] IN ('Selected', 'AllAuthorized')),
    CONSTRAINT [CK_HistoryAudit_Status] CHECK ([Status] = 'AuthorizedRelease'),
    CONSTRAINT [FK_HistoryAccessAudits_LocalUsers_ActorUserId] FOREIGN KEY ([ActorUserId]) REFERENCES [LocalUsers] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [HistoryExportLeases] (
    [UserId] uniqueidentifier NOT NULL,
    [Owner] uniqueidentifier NOT NULL,
    [ExpiresAt] datetimeoffset(7) NOT NULL,
    CONSTRAINT [PK_HistoryExportLeases] PRIMARY KEY ([UserId])
);

CREATE TABLE [HistoryAccessAuditWorkshops] (
    [AuditId] uniqueidentifier NOT NULL,
    [WorkshopId] nvarchar(256) NOT NULL,
    CONSTRAINT [PK_HistoryAccessAuditWorkshops] PRIMARY KEY ([AuditId], [WorkshopId]),
    CONSTRAINT [FK_HistoryAccessAuditWorkshops_HistoryAccessAudits_AuditId] FOREIGN KEY ([AuditId]) REFERENCES [HistoryAccessAudits] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_HistoryAccessAuditWorkshops_WorkshopAccess_WorkshopId] FOREIGN KEY ([WorkshopId]) REFERENCES [WorkshopAccess] ([Id]) ON DELETE NO ACTION
);

CREATE INDEX [IX_VehicleStays_WorkshopId_ExitAt_StayOrderKey] ON [VehicleStays] ([WorkshopId], [ExitAt], [StayOrderKey]) WHERE [AbsorbedByStayId] IS NULL;

CREATE INDEX [IX_VehicleStays_WorkshopId_State_EntryAt_StayOrderKey] ON [VehicleStays] ([WorkshopId], [State], [EntryAt], [StayOrderKey]) WHERE [AbsorbedByStayId] IS NULL;

CREATE INDEX [IX_LprEvents_WorkshopId_ReceivedAt_EventOrderKey] ON [LprEvents] ([WorkshopId], [ReceivedAt], [EventOrderKey]);

CREATE INDEX [IX_HistoryAccessAudits_ActorUserId_OccurredAt_Id] ON [HistoryAccessAudits] ([ActorUserId], [OccurredAt], [Id]);

CREATE INDEX [IX_HistoryAccessAudits_OccurredAt_Id] ON [HistoryAccessAudits] ([OccurredAt], [Id]);

CREATE INDEX [IX_HistoryAccessAuditWorkshops_WorkshopId] ON [HistoryAccessAuditWorkshops] ([WorkshopId]);

CREATE INDEX [IX_HistoryExportLeases_ExpiresAt] ON [HistoryExportLeases] ([ExpiresAt]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261119090322_VehicleHistoryQueries', N'10.0.12');

COMMIT;
GO

