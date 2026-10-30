-- Local synthetic source only. Run manually; the API never creates or seeds this database.
USE [master];
GO
IF DB_ID(N'GarajeMetriaAux') IS NULL
    CREATE DATABASE [GarajeMetriaAux];
GO
USE [GarajeMetriaAux];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.Workshops', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Workshops
    (
        Code nvarchar(12) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Name nvarchar(256) NOT NULL,
        IsActive bit NOT NULL,
        CONSTRAINT PK_Workshops PRIMARY KEY (Code),
        CONSTRAINT CK_Workshops_Code CHECK (LEN(Code) > 0 AND DATALENGTH(Code) = DATALENGTH(RTRIM(Code))),
        CONSTRAINT CK_Workshops_Name CHECK (LEN(Name) > 0)
    );
END;
-- Insert only missing examples; never overwrite existing source data on a rerun.
;WITH Numbers AS
(
    SELECT 1 AS Number
    UNION ALL SELECT Number + 1 FROM Numbers WHERE Number < 30
)
INSERT INTO dbo.Workshops (Code, Name, IsActive)
SELECT RIGHT(N'000000' + CONVERT(nvarchar(6), Number), 6),
       N'Taller de prueba ' + RIGHT(N'00' + CONVERT(nvarchar(2), Number), 2), 1
FROM Numbers
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.Workshops WITH (UPDLOCK, HOLDLOCK)
    WHERE Code = RIGHT(N'000000' + CONVERT(nvarchar(6), Number), 6)
);
COMMIT;
GO
CREATE OR ALTER VIEW dbo.ActiveWorkshops
AS
    SELECT Code, Name FROM dbo.Workshops WHERE IsActive = 1;
GO
