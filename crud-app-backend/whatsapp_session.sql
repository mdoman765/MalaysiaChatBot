-- ============================================================
--  SPRO WhatsApp Bot  –  Session & Audit Tables
--  MSSQL Server 2016+
-- ============================================================

-- ------------------------------------------------------------
--  1. WhatsApp Sessions  (one row per phone number)
-- ------------------------------------------------------------
IF OBJECT_ID('dbo.WhatsAppSessions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WhatsAppSessions (
        Phone           NVARCHAR(30)    NOT NULL,           -- WhatsApp phone number (key)
        CurrentStep     NVARCHAR(50)    NOT NULL            -- state machine value
                            DEFAULT 'INIT',
        TempData        NVARCHAR(MAX)   NOT NULL            -- JSON blob (menu_map, cart, etc.)
                            DEFAULT '{}',
        PreviousStep    NVARCHAR(50)    NOT NULL
                            DEFAULT 'INIT',
        PendingReport   BIT             NOT NULL DEFAULT 0,
        PendingShopReg  BIT             NOT NULL DEFAULT 0,
        CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_WhatsAppSessions PRIMARY KEY CLUSTERED (Phone)
    );
END
GO

-- ------------------------------------------------------------
--  2. Session History / Audit log  (optional but useful)
--     Keeps every state transition for debugging
-- ------------------------------------------------------------
IF OBJECT_ID('dbo.WhatsAppSessionHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WhatsAppSessionHistory (
        Id              BIGINT          IDENTITY(1,1) NOT NULL,
        Phone           NVARCHAR(30)    NOT NULL,
        FromStep        NVARCHAR(50)    NOT NULL,
        ToStep          NVARCHAR(50)    NOT NULL,
        RawMessage      NVARCHAR(1000)  NULL,
        TempDataSnapshot NVARCHAR(MAX)  NULL,
        CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_WhatsAppSessionHistory PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SessionHistory_Phone
            FOREIGN KEY (Phone) REFERENCES dbo.WhatsAppSessions(Phone)
            ON DELETE CASCADE
    );

    CREATE NONCLUSTERED INDEX IX_SessionHistory_Phone
        ON dbo.WhatsAppSessionHistory (Phone, CreatedAt DESC);
END
GO

-- ------------------------------------------------------------
--  3. Stored procedure: Upsert session  (atomic, no race condition)
-- ------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_UpsertWhatsAppSession
    @Phone          NVARCHAR(30),
    @CurrentStep    NVARCHAR(50),
    @TempData       NVARCHAR(MAX),
    @PreviousStep   NVARCHAR(50),
    @PendingReport  BIT,
    @PendingShopReg BIT,
    @RawMessage     NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @FromStep NVARCHAR(50);

    -- Snapshot old step for history
    SELECT @FromStep = CurrentStep
    FROM   dbo.WhatsAppSessions WITH (UPDLOCK, HOLDLOCK)
    WHERE  Phone = @Phone;

    -- Upsert
    MERGE dbo.WhatsAppSessions WITH (HOLDLOCK) AS target
    USING (SELECT @Phone AS Phone) AS source
    ON target.Phone = source.Phone
    WHEN MATCHED THEN
        UPDATE SET
            CurrentStep     = @CurrentStep,
            TempData        = @TempData,
            PreviousStep    = @PreviousStep,
            PendingReport   = @PendingReport,
            PendingShopReg  = @PendingShopReg,
            UpdatedAt       = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (Phone, CurrentStep, TempData, PreviousStep, PendingReport, PendingShopReg)
        VALUES (@Phone, @CurrentStep, @TempData, @PreviousStep, @PendingReport, @PendingShopReg);

    -- Write history row
    INSERT INTO dbo.WhatsAppSessionHistory
            (Phone, FromStep, ToStep, RawMessage, TempDataSnapshot)
    VALUES  (@Phone,
             ISNULL(@FromStep, 'NEW'),
             @CurrentStep,
             @RawMessage,
             @TempData);
END
GO

-- ------------------------------------------------------------
--  Quick verification
-- ------------------------------------------------------------
SELECT 'WhatsAppSessions'        AS [Table], COUNT(*) AS Rows FROM dbo.WhatsAppSessions
UNION ALL
SELECT 'WhatsAppSessionHistory'  AS [Table], COUNT(*) AS Rows FROM dbo.WhatsAppSessionHistory;
GO
