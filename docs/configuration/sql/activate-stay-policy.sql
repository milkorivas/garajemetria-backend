-- EXPLICIT DEPLOYMENT OPERATION. Review target, migration and values before execution.
-- Requires 20261117125011_StayPersistence. Never run from application startup.
-- Empty expected version means first activation only; otherwise use the observed configuration Version.
-- Match the Stays business values of the nodes that will accept new ingestion. Operational values are local.
IF @@TRANCOUNT <> 0 THROW 51001, 'Stay policy activation requires a standalone transaction.', 1;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;

DECLARE @ExpectedVersion uniqueidentifier = '00000000-0000-0000-0000-000000000000';
DECLARE @EstimatedDurationMinutes int = 30;
DECLARE @RepeatWindowSeconds int = 60;
DECLARE @MaximumAssociationHours int = 48;

IF @ExpectedVersion IS NULL OR @EstimatedDurationMinutes IS NULL OR @RepeatWindowSeconds IS NULL
   OR @MaximumAssociationHours IS NULL OR @EstimatedDurationMinutes <= 0
   OR @RepeatWindowSeconds <= 0 OR @MaximumAssociationHours <= 0
    THROW 51001, 'Invalid stay policy durations.', 1;

-- Checked bigint arithmetic; an unrepresentable duration fails before opening the transaction.
DECLARE @EstimatedTicks bigint = CONVERT(bigint, @EstimatedDurationMinutes) * 600000000;
DECLARE @RepeatTicks bigint = CONVERT(bigint, @RepeatWindowSeconds) * 10000000;
DECLARE @MaximumTicks bigint = CONVERT(bigint, @MaximumAssociationHours) * 36000000000;
-- ASCII-only canonical UTF-8 representation, identical to StayOptions.Fingerprint (never hash nvarchar).
DECLARE @Canonical varchar(256) = CONCAT('garajemetria-stays:v1|', @EstimatedTicks, '|', @RepeatTicks,
    '|', @MaximumTicks, '|Atlantic/Canary|1');
DECLARE @Fingerprint varchar(64) = CONVERT(varchar(64), HASHBYTES('SHA2_256', @Canonical), 2);

BEGIN TRY
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'workshops:catalog',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
    IF @LockResult < 0 THROW 51001, 'Stay policy activation lock unavailable.', 1;

    DECLARE @Version uniqueidentifier, @ActivePolicyId uniqueidentifier, @PolicyId uniqueidentifier;
    SELECT @Version = [Version], @ActivePolicyId = [ActivePolicyId] FROM [StayConfiguration] WHERE [Id] = 1;
    IF (@Version IS NULL AND @ExpectedVersion <> '00000000-0000-0000-0000-000000000000')
       OR (@Version IS NOT NULL AND @Version <> @ExpectedVersion)
        THROW 51001, 'Stay configuration version changed. Review before retrying.', 1;

    DECLARE @Now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
    DECLARE @NowTicks bigint = DATEDIFF_BIG(MICROSECOND, CONVERT(datetime2(7), '0001-01-01', 23),
        CONVERT(datetime2(7), @Now)) * 10 + (DATEPART(NANOSECOND, @Now) % 1000) / 100;
    IF @EstimatedTicks > @NowTicks OR @RepeatTicks > @NowTicks OR @MaximumTicks > @NowTicks
       OR @EstimatedTicks > 3155378975999999999 - @NowTicks
       OR @RepeatTicks > 3155378975999999999 - @NowTicks
       OR @MaximumTicks > 3155378975999999999 - @NowTicks
        THROW 51001, 'Stay durations exceed supported date arithmetic.', 1;

    SELECT @PolicyId = [Id] FROM [StayPolicies] WHERE [Fingerprint] = @Fingerprint;
    IF @PolicyId IS NULL
    BEGIN
        SET @PolicyId = NEWID();
        INSERT INTO [StayPolicies] ([Id], [Fingerprint], [EstimatedDurationTicks], [RepeatWindowTicks],
            [MaximumAssociationTicks], [TimeZoneId], [AlgorithmVersion])
        VALUES (@PolicyId, @Fingerprint, @EstimatedTicks, @RepeatTicks, @MaximumTicks, N'Atlantic/Canary', 1);
    END
    ELSE IF NOT EXISTS (SELECT 1 FROM [StayPolicies] WHERE [Id] = @PolicyId
        AND [EstimatedDurationTicks] = @EstimatedTicks AND [RepeatWindowTicks] = @RepeatTicks
        AND [MaximumAssociationTicks] = @MaximumTicks AND [TimeZoneId] = N'Atlantic/Canary' AND [AlgorithmVersion] = 1)
        THROW 51001, 'The immutable policy does not match its fingerprint.', 1;

    IF @Version IS NULL
        INSERT INTO [StayConfiguration] ([Id], [ActivePolicyId], [Version], [ActivatedAt])
        VALUES (1, @PolicyId, NEWID(), @Now);
    ELSE IF @ActivePolicyId <> @PolicyId
    BEGIN
        IF EXISTS (SELECT 1 FROM [StayConfiguration] WHERE [Id] = 1 AND [ActivatedAt] > @Now)
            THROW 51001, 'Activation time cannot move backwards.', 1;
        UPDATE [StayConfiguration] SET [ActivePolicyId] = @PolicyId, [Version] = NEWID(), [ActivatedAt] = @Now
        WHERE [Id] = 1 AND [Version] = @ExpectedVersion;
        IF @@ROWCOUNT <> 1 THROW 51001, 'Stay configuration version changed.', 1;
    END;
    COMMIT TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    SELECT [ActivePolicyId], [Version], [ActivatedAt] FROM [StayConfiguration] WHERE [Id] = 1;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    THROW;
END CATCH;
