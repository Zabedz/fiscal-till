-- Fiscal journal: gapless receipt numbers per till and the end-of-day Z-report. Safe to run repeatedly.
-- sqlcmd variables: JOURNAL_DB_NAME (database to create or update) and JOURNAL_APP_PASSWORD (the worker's login).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF DB_ID(N'$(JOURNAL_DB_NAME)') IS NULL
    CREATE DATABASE [$(JOURNAL_DB_NAME)];
GO

-- Lets dbo.ZReport read one consistent snapshot across its two result sets.
ALTER DATABASE [$(JOURNAL_DB_NAME)] SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

-- The worker's login may run the journal procedures and read the gap view; journal rows change only through dbo.RecordSale.
IF SUSER_ID(N'journal_app') IS NULL
    CREATE LOGIN journal_app WITH PASSWORD = N'$(JOURNAL_APP_PASSWORD)';
ELSE
    ALTER LOGIN journal_app WITH PASSWORD = N'$(JOURNAL_APP_PASSWORD)';
GO

USE [$(JOURNAL_DB_NAME)];
GO

IF USER_ID(N'journal_app') IS NULL
    CREATE USER journal_app FOR LOGIN journal_app;
GO

IF OBJECT_ID(N'dbo.TillCounter', N'U') IS NULL
    CREATE TABLE dbo.TillCounter
    (
        TillKey       nvarchar(40) NOT NULL CONSTRAINT PK_TillCounter PRIMARY KEY,
        LastReceiptNo int          NOT NULL
    );
GO

IF OBJECT_ID(N'dbo.JournalEntry', N'U') IS NULL
    CREATE TABLE dbo.JournalEntry
    (
        JournalEntryId    bigint IDENTITY    NOT NULL CONSTRAINT PK_JournalEntry PRIMARY KEY,
        SaleId            char(32)           NOT NULL CONSTRAINT UQ_JournalEntry_SaleId UNIQUE,
        VenueId           nvarchar(20)       NOT NULL,
        TillId            nvarchar(4)        NOT NULL,
        TillKey           nvarchar(40)       NOT NULL,
        ReceiptNo         int                NOT NULL,
        Market            char(2)            NOT NULL,
        Currency          char(3)            NOT NULL,
        BusinessDate      date               NOT NULL,
        SoldAt            datetimeoffset(7)  NOT NULL,
        GrossCents        bigint             NOT NULL,
        VatCents          bigint             NOT NULL,
        PaymentMethod     varchar(8)         NOT NULL,
        SignatureDeviceId nvarchar(128)      NOT NULL,
        SignatureCounter  bigint             NOT NULL,
        SignatureValue    nvarchar(512)      NOT NULL,
        SignedAt          datetimeoffset(7)  NOT NULL,
        JournaledAt       datetimeoffset(3)  NOT NULL CONSTRAINT DF_JournalEntry_JournaledAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT UQ_JournalEntry_TillKey_ReceiptNo UNIQUE (TillKey, ReceiptNo)
    );
GO

-- Widens tables made by earlier versions of this script: longer device ids and signatures, full-precision timestamps.
IF COL_LENGTH(N'dbo.JournalEntry', N'SignatureDeviceId') < 256
    ALTER TABLE dbo.JournalEntry ALTER COLUMN SignatureDeviceId nvarchar(128) NOT NULL;
IF COL_LENGTH(N'dbo.JournalEntry', N'SignatureValue') < 1024
    ALTER TABLE dbo.JournalEntry ALTER COLUMN SignatureValue nvarchar(512) NOT NULL;
IF (SELECT scale FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.JournalEntry') AND name = N'SoldAt') < 7
    ALTER TABLE dbo.JournalEntry ALTER COLUMN SoldAt datetimeoffset(7) NOT NULL;
IF (SELECT scale FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.JournalEntry') AND name = N'SignedAt') < 7
    ALTER TABLE dbo.JournalEntry ALTER COLUMN SignedAt datetimeoffset(7) NOT NULL;
GO

IF OBJECT_ID(N'dbo.JournalLine', N'U') IS NULL
    CREATE TABLE dbo.JournalLine
    (
        JournalEntryId bigint        NOT NULL CONSTRAINT FK_JournalLine_JournalEntry REFERENCES dbo.JournalEntry (JournalEntryId),
        LineNumber     int           NOT NULL,
        Sku            nvarchar(32)  NOT NULL,
        Description    nvarchar(80)  NOT NULL,
        Category       nvarchar(40)  NOT NULL,
        Quantity       int           NOT NULL,
        UnitPriceCents bigint        NOT NULL,
        VatRate        decimal(5, 2) NOT NULL,
        GrossCents     bigint        NOT NULL,
        VatCents       bigint        NOT NULL,
        CONSTRAINT PK_JournalLine PRIMARY KEY (JournalEntryId, LineNumber)
    );
GO

-- Records a signed sale and returns its receipt number; a sale already recorded keeps its number.
-- Parameters are wider than their columns, so an over-long value fails the INSERT (error 2628) and rolls back.
CREATE OR ALTER PROCEDURE dbo.RecordSale
    @SaleId            varchar(64),
    @VenueId           nvarchar(100),
    @TillId            nvarchar(100),
    @Market            varchar(16),
    @Currency          varchar(16),
    @BusinessDate      date,
    @SoldAt            datetimeoffset(7),
    @GrossCents        bigint,
    @VatCents          bigint,
    @PaymentMethod     varchar(32),
    @SignatureDeviceId nvarchar(4000),
    @SignatureCounter  bigint,
    @SignatureValue    nvarchar(4000),
    @SignedAt          datetimeoffset(7),
    @LinesJson         nvarchar(max),
    @ReceiptNo         int OUTPUT,
    @AlreadyRecorded   bit OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    -- Any error rolls back the whole transaction, counter increment included, so numbers stay gapless.
    SET XACT_ABORT ON;

    -- OUTPUT parameters also carry the caller's value in; start from nothing.
    SET @ReceiptNo = NULL;
    SET @AlreadyRecorded = 0;

    DECLARE @TillKey nvarchar(201) = CONCAT(@VenueId, N'-', @TillId);

    BEGIN TRANSACTION;

    -- UPDLOCK, HOLDLOCK holds the till's row (or its key range on a first sale) to commit, so each till numbers one sale at a time.
    IF NOT EXISTS (SELECT 1 FROM dbo.TillCounter WITH (UPDLOCK, HOLDLOCK) WHERE TillKey = @TillKey)
        INSERT dbo.TillCounter (TillKey, LastReceiptNo) VALUES (@TillKey, 0);

    SET @ReceiptNo = (SELECT ReceiptNo FROM dbo.JournalEntry WHERE SaleId = @SaleId);
    IF @ReceiptNo IS NOT NULL
    BEGIN
        SET @AlreadyRecorded = 1;
        COMMIT;
        RETURN;
    END;

    UPDATE dbo.TillCounter SET @ReceiptNo = LastReceiptNo = LastReceiptNo + 1 WHERE TillKey = @TillKey;

    INSERT dbo.JournalEntry
        (SaleId, VenueId, TillId, TillKey, ReceiptNo, Market, Currency, BusinessDate, SoldAt, GrossCents, VatCents,
         PaymentMethod, SignatureDeviceId, SignatureCounter, SignatureValue, SignedAt)
    VALUES
        (@SaleId, @VenueId, @TillId, @TillKey, @ReceiptNo, @Market, @Currency, @BusinessDate, @SoldAt, @GrossCents, @VatCents,
         @PaymentMethod, @SignatureDeviceId, @SignatureCounter, @SignatureValue, @SignedAt);

    DECLARE @EntryId bigint = SCOPE_IDENTITY();

    INSERT dbo.JournalLine (JournalEntryId, LineNumber, Sku, Description, Category, Quantity, UnitPriceCents, VatRate, GrossCents, VatCents)
    SELECT @EntryId, CAST(item.[key] AS int) + 1, line.Sku, line.Description, line.Category, line.Quantity,
           line.UnitPriceCents, line.VatRate, line.GrossCents, line.VatCents
    FROM OPENJSON(@LinesJson) AS item
    CROSS APPLY OPENJSON(item.value) WITH
    (
        Sku            nvarchar(4000) '$.sku',
        Description    nvarchar(4000) '$.description',
        Category       nvarchar(4000) '$.category',
        Quantity       int            '$.quantity',
        UnitPriceCents bigint         '$.unitPriceCents',
        VatRate        decimal(5, 2)  '$.vatRate',
        GrossCents     bigint         '$.grossCents',
        VatCents       bigint         '$.vatCents'
    ) AS line;

    COMMIT;
END;
GO

-- End-of-day report for one venue and trading day: VAT per rate per till with totals, then each receipt with a running total.
CREATE OR ALTER PROCEDURE dbo.ZReport
    @VenueId      nvarchar(20),
    @BusinessDate date
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    SELECT
        e.TillId,
        l.VatRate,
        COUNT(DISTINCT e.SaleId)          AS Sales,
        SUM(l.GrossCents)                 AS GrossCents,
        SUM(l.GrossCents - l.VatCents)    AS NetCents,
        SUM(l.VatCents)                   AS VatCents,
        GROUPING(e.TillId)                AS AllTills,
        GROUPING(l.VatRate)               AS AllRates
    FROM dbo.JournalEntry AS e
    JOIN dbo.JournalLine AS l ON l.JournalEntryId = e.JournalEntryId
    WHERE e.VenueId = @VenueId AND e.BusinessDate = @BusinessDate
    GROUP BY GROUPING SETS ((e.TillId, l.VatRate), (e.TillId), (l.VatRate), ())
    ORDER BY GROUPING(e.TillId), e.TillId, GROUPING(l.VatRate), l.VatRate DESC;

    -- The step is measured over the till's whole history: receipts of different trading days interleave after a DLQ redrive.
    WITH Receipts AS
    (
        SELECT e.*, e.ReceiptNo - LAG(e.ReceiptNo) OVER (PARTITION BY e.TillKey ORDER BY e.ReceiptNo) AS StepFromPrevious
        FROM dbo.JournalEntry AS e
        WHERE e.VenueId = @VenueId
    )
    SELECT
        r.TillId,
        r.ReceiptNo,
        r.SaleId,
        r.SoldAt,
        r.GrossCents,
        SUM(r.GrossCents) OVER (PARTITION BY r.TillKey ORDER BY r.ReceiptNo ROWS UNBOUNDED PRECEDING) AS RunningGrossCents,
        r.StepFromPrevious
    FROM Receipts AS r
    WHERE r.BusinessDate = @BusinessDate
    ORDER BY r.TillId, r.ReceiptNo;

    COMMIT;
END;
GO

-- One row per till: receipts held against the numbers its counter has issued (MissingNumbers is 0 when gapless).
CREATE OR ALTER VIEW dbo.ReceiptGaps
AS
SELECT
    c.TillKey,
    COUNT(e.ReceiptNo)                    AS Receipts,
    MIN(e.ReceiptNo)                      AS FirstReceiptNo,
    c.LastReceiptNo,
    c.LastReceiptNo - COUNT(e.ReceiptNo)  AS MissingNumbers
FROM dbo.TillCounter AS c
LEFT JOIN dbo.JournalEntry AS e ON e.TillKey = c.TillKey
GROUP BY c.TillKey, c.LastReceiptNo;
GO

GRANT EXECUTE ON dbo.RecordSale TO journal_app;
GRANT EXECUTE ON dbo.ZReport TO journal_app;
GRANT SELECT ON dbo.ReceiptGaps TO journal_app;
GO
