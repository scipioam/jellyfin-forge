namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Known schema migrations for the Danmuku database. The initial migration creates the
/// complete M1 schema described in design chapter 3.
/// </summary>
public static class SchemaMigrations
{
    /// <summary>
    /// Gets the schema version this plugin build expects.
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>
    /// Gets the default migration list used in production.
    /// </summary>
    public static IReadOnlyList<SchemaMigration> Default { get; } =
    [
        new SchemaMigration(1, "Initial Danmuku schema", InitialSchemaSql),
        new SchemaMigration(2, "Add publish intent fields to ImportTasks", PublishIntentSql),
        new SchemaMigration(3, "Import lifecycle and media checks", ImportLifecycleSql, RebuildsReferencedTables: true),
        new SchemaMigration(4, "Allow file-only import batches", IndependentImportSql, RebuildsReferencedTables: true)
    ];

    private const string IndependentImportSql = """
        CREATE TABLE ImportBatches_new (
            BatchId TEXT NOT NULL PRIMARY KEY,
            MediaId TEXT NULL,
            Operation TEXT NOT NULL DEFAULT 'append' CHECK (Operation IN ('import','append','replace')),
            ReplaceFileId TEXT NULL,
            ExpectedMediaVersion INTEGER NULL,
            Status TEXT NOT NULL DEFAULT 'Open' CHECK (Status IN ('Open','Finished')),
            CreatedAtUtcMs INTEGER NOT NULL,
            FinishedAtUtcMs INTEGER NULL,
            CHECK (
                (Operation = 'import' AND MediaId IS NULL AND ExpectedMediaVersion IS NULL AND ReplaceFileId IS NULL)
                OR (Operation IN ('append','replace') AND MediaId IS NOT NULL AND length(trim(MediaId)) > 0
                    AND ExpectedMediaVersion IS NOT NULL AND ExpectedMediaVersion >= 0
                    AND ((Operation = 'replace' AND ReplaceFileId IS NOT NULL)
                        OR (Operation = 'append' AND ReplaceFileId IS NULL)))
            )
        );
        INSERT INTO ImportBatches_new SELECT * FROM ImportBatches;
        DROP TABLE ImportBatches;
        ALTER TABLE ImportBatches_new RENAME TO ImportBatches;
        CREATE INDEX IX_ImportBatches_MediaId ON ImportBatches (MediaId);
        CREATE INDEX IX_ImportBatches_FinishedAt ON ImportBatches (FinishedAtUtcMs);
        """;

    private const string ImportLifecycleSql = """
        CREATE TABLE ImportBatches_new (
            BatchId TEXT NOT NULL PRIMARY KEY,
            MediaId TEXT NOT NULL,
            Operation TEXT NOT NULL DEFAULT 'append' CHECK (Operation IN ('append','replace')),
            ReplaceFileId TEXT NULL,
            ExpectedMediaVersion INTEGER NOT NULL CHECK (ExpectedMediaVersion >= 0),
            Status TEXT NOT NULL DEFAULT 'Open' CHECK (Status IN ('Open','Finished')),
            CreatedAtUtcMs INTEGER NOT NULL,
            FinishedAtUtcMs INTEGER NULL,
            CHECK ((Operation = 'replace') = (ReplaceFileId IS NOT NULL))
        );
        INSERT INTO ImportBatches_new SELECT * FROM ImportBatches;
        DROP TABLE ImportBatches;
        ALTER TABLE ImportBatches_new RENAME TO ImportBatches;
        CREATE INDEX IX_ImportBatches_MediaId ON ImportBatches(MediaId);
        CREATE INDEX IX_ImportBatches_FinishedAt ON ImportBatches(FinishedAtUtcMs);
        DROP INDEX IX_Files_ContentHash;
        CREATE UNIQUE INDEX IX_Files_ContentHash ON Files(ContentHash);
        CREATE UNIQUE INDEX IX_Files_StoredFileName ON Files(StoredFileName COLLATE NOCASE);
        ALTER TABLE ImportSlots ADD COLUMN OriginalFileName TEXT NULL;
        ALTER TABLE ImportSlots ADD COLUMN UploadPath TEXT NULL;
        ALTER TABLE ImportSlots ADD COLUMN CleanupError TEXT NULL;
        ALTER TABLE ImportSlots ADD COLUMN LastProgressAtUtcMs INTEGER NULL;
        ALTER TABLE ImportSlots ADD COLUMN TemporaryBytes INTEGER NOT NULL DEFAULT 0 CHECK (TemporaryBytes >= 0);
        ALTER TABLE ImportTasks ADD COLUMN ResultCode TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN ParseStatisticsJson TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN ErrorStorageBytes INTEGER NOT NULL DEFAULT 0 CHECK (ErrorStorageBytes >= 0);
        ALTER TABLE ImportTasks ADD COLUMN SkipConfirmed INTEGER NOT NULL DEFAULT 0 CHECK (SkipConfirmed IN (0,1));
        ALTER TABLE MediaState ADD COLUMN CheckStatus TEXT NOT NULL DEFAULT 'Unchecked'
            CHECK (CheckStatus IN ('Unchecked','Exists','Missing','CheckFailed'));
        ALTER TABLE MediaState ADD COLUMN CheckedAtUtcMs INTEGER NULL;
        CREATE TABLE BindingCheckJobs (
            JobId TEXT NOT NULL PRIMARY KEY,
            Status TEXT NOT NULL CHECK (Status IN ('Queued','Processing','Completed','Interrupted','Failed')),
            Processed INTEGER NOT NULL DEFAULT 0,
            Missing INTEGER NOT NULL DEFAULT 0,
            Failed INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtcMs INTEGER NOT NULL,
            FinishedAtUtcMs INTEGER NULL
        );
        CREATE UNIQUE INDEX IX_BindingCheckJobs_Active ON BindingCheckJobs((1)) WHERE Status IN ('Queued','Processing');
        """;

    private const string PublishIntentSql = """
        ALTER TABLE ImportTasks ADD COLUMN TargetFileId TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetOriginalFileName TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetStoredFileName TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetDisplayName TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetFormat TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetContentHash TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetLastCommentTimeMs INTEGER NULL;
        ALTER TABLE ImportTasks ADD COLUMN TargetParseDataVersion TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN StagedOriginalPath TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN StagedAssetPath TEXT NULL;
        ALTER TABLE ImportTasks ADD COLUMN IntentCreatedAtUtcMs INTEGER NULL;
        """;

    private const string InitialSchemaSql = """
        CREATE TABLE SchemaVersion (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
            Version INTEGER NOT NULL,
            AppliedAtUtcMs INTEGER NOT NULL
        );

        CREATE TABLE Files (
            FileId TEXT NOT NULL PRIMARY KEY,
            OriginalFileName TEXT NOT NULL,
            StoredFileName TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            Format TEXT NOT NULL CHECK (Format IN ('xml', 'json')),
            ContentHash TEXT NOT NULL,
            ImportedAtUtcMs INTEGER NOT NULL,
            CommentCount INTEGER NOT NULL DEFAULT 0 CHECK (CommentCount >= 0),
            LastCommentTimeMs INTEGER NULL CHECK (LastCommentTimeMs IS NULL OR LastCommentTimeMs >= 0),
            ParseDataVersion TEXT NOT NULL,
            Status TEXT NOT NULL DEFAULT 'Published' CHECK (Status IN ('Published', 'Deleting', 'DeleteFailed')),
            DeleteRequestedAtUtcMs INTEGER NULL
        );

        CREATE INDEX IX_Files_ContentHash ON Files (ContentHash);

        CREATE TABLE Comments (
            CommentId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            FileId TEXT NOT NULL REFERENCES Files (FileId) ON DELETE CASCADE,
            SourceOrdinal INTEGER NOT NULL CHECK (SourceOrdinal >= 0),
            SourceId TEXT NULL,
            TimeMs INTEGER NOT NULL CHECK (TimeMs >= 0),
            Text TEXT NOT NULL,
            Color INTEGER NOT NULL,
            Mode INTEGER NOT NULL,
            FontSize INTEGER NOT NULL,
            SourceTimeMs INTEGER NULL CHECK (SourceTimeMs IS NULL OR SourceTimeMs >= 0),
            SenderHash TEXT NULL,
            SourceNumericId TEXT NULL,
            Weight INTEGER NULL,
            Attr INTEGER NULL,
            Pool INTEGER NULL
        );

        CREATE INDEX IX_Comments_File_Time ON Comments (FileId, TimeMs, SourceOrdinal);

        CREATE TABLE MediaBindings (
            MediaId TEXT NOT NULL,
            FileId TEXT NOT NULL REFERENCES Files (FileId) ON DELETE RESTRICT,
            BoundAtUtcMs INTEGER NOT NULL,
            PRIMARY KEY (MediaId, FileId)
        );

        CREATE INDEX IX_MediaBindings_FileId ON MediaBindings (FileId);

        CREATE TABLE MediaState (
            MediaId TEXT NOT NULL PRIMARY KEY,
            ActiveFileId TEXT NULL,
            IsDeactivated INTEGER NOT NULL DEFAULT 0 CHECK (IsDeactivated IN (0, 1)),
            Version INTEGER NOT NULL DEFAULT 0 CHECK (Version >= 0),
            CHECK (IsDeactivated = 0 OR ActiveFileId IS NULL),
            FOREIGN KEY (MediaId, ActiveFileId) REFERENCES MediaBindings (MediaId, FileId) DEFERRABLE INITIALLY DEFERRED
        );

        CREATE TABLE ImportBatches (
            BatchId TEXT NOT NULL PRIMARY KEY,
            MediaId TEXT NOT NULL,
            Operation TEXT NOT NULL DEFAULT 'append' CHECK (Operation IN ('append', 'replace')),
            ReplaceFileId TEXT NULL REFERENCES Files (FileId) ON DELETE SET NULL,
            ExpectedMediaVersion INTEGER NOT NULL CHECK (ExpectedMediaVersion >= 0),
            Status TEXT NOT NULL DEFAULT 'Open' CHECK (Status IN ('Open', 'Finished')),
            CreatedAtUtcMs INTEGER NOT NULL,
            FinishedAtUtcMs INTEGER NULL,
            CHECK ((Operation = 'replace') = (ReplaceFileId IS NOT NULL))
        );

        CREATE INDEX IX_ImportBatches_MediaId ON ImportBatches (MediaId);
        CREATE INDEX IX_ImportBatches_FinishedAt ON ImportBatches (FinishedAtUtcMs);

        CREATE TABLE ImportSlots (
            BatchId TEXT NOT NULL REFERENCES ImportBatches (BatchId) ON DELETE CASCADE,
            Slot INTEGER NOT NULL CHECK (Slot >= 0),
            Status TEXT NOT NULL DEFAULT 'PendingUpload' CHECK (Status IN ('PendingUpload', 'Receiving', 'Accepted', 'Completed', 'Failed', 'Cancelled', 'Expired', 'Interrupted')),
            TaskId TEXT NULL REFERENCES ImportTasks (TaskId) DEFERRABLE INITIALLY DEFERRED,
            TotalBytes INTEGER NULL CHECK (TotalBytes IS NULL OR TotalBytes >= 0),
            ReceivedBytes INTEGER NOT NULL DEFAULT 0 CHECK (ReceivedBytes >= 0),
            CreatedAtUtcMs INTEGER NOT NULL,
            UploadDeadlineAtUtcMs INTEGER NOT NULL,
            ReceivingStartedAtUtcMs INTEGER NULL,
            ReceivingDeadlineAtUtcMs INTEGER NULL,
            FinishedAtUtcMs INTEGER NULL,
            ErrorCode TEXT NULL,
            PRIMARY KEY (BatchId, Slot),
            CHECK (TotalBytes IS NULL OR ReceivedBytes <= TotalBytes)
        );

        CREATE TABLE ImportTasks (
            TaskId TEXT NOT NULL PRIMARY KEY,
            BatchId TEXT NOT NULL,
            Slot INTEGER NOT NULL CHECK (Slot >= 0),
            FileId TEXT NULL REFERENCES Files (FileId) ON DELETE SET NULL,
            Status TEXT NOT NULL CHECK (Status IN ('Queued', 'Processing', 'AwaitingConfirmation', 'AwaitingConflictResolution', 'Completed', 'Failed', 'Cancelled', 'Expired', 'Interrupted')),
            Stage TEXT NULL,
            StagePercent REAL NULL CHECK (StagePercent IS NULL OR (StagePercent >= 0 AND StagePercent <= 100)),
            TotalComments INTEGER NULL CHECK (TotalComments IS NULL OR TotalComments >= 0),
            ProcessedComments INTEGER NOT NULL DEFAULT 0 CHECK (ProcessedComments >= 0),
            NormalComments INTEGER NOT NULL DEFAULT 0 CHECK (NormalComments >= 0),
            AbnormalComments INTEGER NOT NULL DEFAULT 0 CHECK (AbnormalComments >= 0),
            ImportedComments INTEGER NOT NULL DEFAULT 0 CHECK (ImportedComments >= 0),
            SkippedComments INTEGER NOT NULL DEFAULT 0 CHECK (SkippedComments >= 0),
            ErrorCode TEXT NULL,
            CreatedAtUtcMs INTEGER NOT NULL,
            StartedAtUtcMs INTEGER NULL,
            FinishedAtUtcMs INTEGER NULL,
            DeadlineAtUtcMs INTEGER NULL,
            UNIQUE (BatchId, Slot),
            FOREIGN KEY (BatchId, Slot) REFERENCES ImportSlots (BatchId, Slot) DEFERRABLE INITIALLY DEFERRED
        );

        CREATE INDEX IX_ImportTasks_FinishedAt ON ImportTasks (FinishedAtUtcMs);

        CREATE TABLE ImportErrors (
            ErrorId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            TaskId TEXT NOT NULL REFERENCES ImportTasks (TaskId) ON DELETE CASCADE,
            SourceOrdinal INTEGER NOT NULL CHECK (SourceOrdinal >= 0),
            ReasonCodes TEXT NOT NULL,
            TextSummary TEXT NULL,
            CreatedAtUtcMs INTEGER NOT NULL
        );

        CREATE INDEX IX_ImportErrors_Task_SourceOrdinal ON ImportErrors (TaskId, SourceOrdinal);

        CREATE TABLE PlaybackRequests (
            PlaybackId TEXT NOT NULL PRIMARY KEY,
            SessionHash TEXT NOT NULL,
            UserId TEXT NOT NULL,
            MediaId TEXT NOT NULL,
            FileId TEXT NULL REFERENCES Files (FileId) ON DELETE SET NULL,
            AlgorithmVersion TEXT NOT NULL,
            LoadLimit INTEGER NOT NULL CHECK (LoadLimit >= 0),
            ConfigSnapshot TEXT NULL,
            Status TEXT NOT NULL DEFAULT 'Active' CHECK (Status IN ('Active', 'Expired', 'Unavailable')),
            CreatedAtUtcMs INTEGER NOT NULL,
            ExpiresAtUtcMs INTEGER NOT NULL
        );

        CREATE INDEX IX_PlaybackRequests_ExpiresAt ON PlaybackRequests (ExpiresAtUtcMs);
        CREATE INDEX IX_PlaybackRequests_SessionHash ON PlaybackRequests (SessionHash);
        CREATE INDEX IX_PlaybackRequests_UserMedia ON PlaybackRequests (UserId, MediaId);
        """;
}
