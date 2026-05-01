-- One-time bootstrap for production DB that was originally created via
-- EnsureCreated() (no __EFMigrationsHistory table). Marks every shipped
-- migration as already applied so the next app start (which now calls
-- Database.Migrate() instead of EnsureCreated) does NOT try to re-apply
-- anything.
--
-- Run this BEFORE deploying the Migrate() code change. Idempotent: re-running
-- is a no-op because of NOT EXISTS guards.
--
-- Apply on the VPS with:
--   docker exec -i orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd \
--     -S localhost -U sa -P "$SQL_PASSWORD" -d OrderDeckLicense -C -No \
--     -i /tmp/bootstrap-migration-history.sql

USE OrderDeckLicense;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'__EFMigrationsHistory')
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId]    NVARCHAR(150) NOT NULL,
        [ProductVersion] NVARCHAR(32)  NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END
GO

-- Migrations are listed in chronological order. ProductVersion matches the
-- snapshot at the time the migration was generated (EF Core 9.0.0 throughout).
DECLARE @v NVARCHAR(32) = N'9.0.0';

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260429092013_InitialSchema')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260429092013_InitialSchema', @v);

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260429203232_AddAuditLog')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260429203232_AddAuditLog', @v);

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260429230903_AddEmailInfra')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260429230903_AddEmailInfra', @v);

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260430062306_AddIntakeForm')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260430062306_AddIntakeForm', @v);

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260430093255_AddSubmissionPhone')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260430093255_AddSubmissionPhone', @v);

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260430153414_AddCustomerBackups')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260430153414_AddCustomerBackups', @v);
GO

SELECT [MigrationId], [ProductVersion] FROM [__EFMigrationsHistory] ORDER BY [MigrationId];
GO
