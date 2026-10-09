-- ============================================
-- RFID Label Printer System - Database Schema
-- Target: Microsoft SQL Server 2019+
-- ============================================

-- Create database (run separately if needed)
-- CREATE DATABASE RfidLabelPrinter;
-- GO
-- USE RfidLabelPrinter;
-- GO

-- ============================================
-- Table: PrintJobs
-- Stores all print job records with status tracking
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PrintJobs')
BEGIN
    CREATE TABLE [dbo].[PrintJobs]
    (
        [Id]            INT             IDENTITY(1,1)   NOT NULL,
        [Barcode]       NVARCHAR(256)                   NOT NULL,
        [LabelZpl]      NVARCHAR(MAX)                   NOT NULL,
        [Status]        NVARCHAR(20)                    NOT NULL DEFAULT 'Pending',
        [RetryCount]    INT                             NOT NULL DEFAULT 0,
        [ErrorMessage]  NVARCHAR(1000)                  NULL,
        [BatchId]       UNIQUEIDENTIFIER                NOT NULL,
        [CreatedAt]     DATETIME2(7)                    NOT NULL DEFAULT SYSUTCDATETIME(),
        [UpdatedAt]     DATETIME2(7)                    NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PrintJobs]       PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [CK_PrintJobs_Status] CHECK ([Status] IN ('Pending', 'Processing', 'Success', 'Failed'))
    );

    -- Index for worker polling: quickly find pending jobs in FIFO order
    CREATE NONCLUSTERED INDEX [IX_PrintJobs_Status_CreatedAt]
        ON [dbo].[PrintJobs] ([Status], [CreatedAt] ASC)
        INCLUDE ([Barcode], [RetryCount], [BatchId]);

    -- Index for batch lookups
    CREATE NONCLUSTERED INDEX [IX_PrintJobs_BatchId]
        ON [dbo].[PrintJobs] ([BatchId])
        INCLUDE ([Status]);

    -- Index for dashboard statistics
    CREATE NONCLUSTERED INDEX [IX_PrintJobs_CreatedAt]
        ON [dbo].[PrintJobs] ([CreatedAt] DESC)
        INCLUDE ([Status]);
END
GO

-- ============================================
-- Table: PrintLogs
-- Audit trail for all print attempts
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PrintLogs')
BEGIN
    CREATE TABLE [dbo].[PrintLogs]
    (
        [Id]            BIGINT          IDENTITY(1,1)   NOT NULL,
        [PrintJobId]    INT                             NOT NULL,
        [Action]        NVARCHAR(50)                    NOT NULL,
        [Success]       BIT                             NOT NULL,
        [Message]       NVARCHAR(2000)                  NULL,
        [ZplSent]       NVARCHAR(MAX)                   NULL,
        [Timestamp]     DATETIME2(7)                    NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PrintLogs]           PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_PrintLogs_PrintJobs] FOREIGN KEY ([PrintJobId])
            REFERENCES [dbo].[PrintJobs] ([Id]) ON DELETE CASCADE
    );

    CREATE NONCLUSTERED INDEX [IX_PrintLogs_PrintJobId]
        ON [dbo].[PrintLogs] ([PrintJobId])
        INCLUDE ([Action], [Success], [Timestamp]);

    CREATE NONCLUSTERED INDEX [IX_PrintLogs_Timestamp]
        ON [dbo].[PrintLogs] ([Timestamp] DESC)
        INCLUDE ([PrintJobId], [Action], [Success]);
END
GO

-- ============================================
-- Stored Procedure: ClaimPendingJobs
-- Atomically claims a batch of pending jobs
-- for processing (prevents race conditions)
-- ============================================
CREATE OR ALTER PROCEDURE [dbo].[ClaimPendingJobs]
    @BatchSize INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ClaimedJobs TABLE (Id INT);

    ;WITH PendingCTE AS (
        SELECT TOP (@BatchSize) [Id], [Status], [UpdatedAt]
        FROM [dbo].[PrintJobs] WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE [Status] = 'Pending'
        ORDER BY [CreatedAt] ASC
    )
    UPDATE PendingCTE
    SET
        [Status] = 'Processing',
        [UpdatedAt] = SYSUTCDATETIME()
    OUTPUT INSERTED.Id INTO @ClaimedJobs;

    SELECT pj.*
    FROM [dbo].[PrintJobs] pj
    INNER JOIN @ClaimedJobs cj ON pj.Id = cj.Id
    ORDER BY pj.[CreatedAt] ASC;
END
GO

-- ============================================
-- Stored Procedure: GetDashboardStats
-- Returns aggregated statistics for the dashboard
-- ============================================
CREATE OR ALTER PROCEDURE [dbo].[GetDashboardStats]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COUNT(*)                                                    AS TotalJobs,
        SUM(CASE WHEN [Status] = 'Pending'    THEN 1 ELSE 0 END)   AS PendingCount,
        SUM(CASE WHEN [Status] = 'Processing' THEN 1 ELSE 0 END)   AS ProcessingCount,
        SUM(CASE WHEN [Status] = 'Success'    THEN 1 ELSE 0 END)   AS SuccessCount,
        SUM(CASE WHEN [Status] = 'Failed'     THEN 1 ELSE 0 END)   AS FailedCount
    FROM [dbo].[PrintJobs];
END
GO
