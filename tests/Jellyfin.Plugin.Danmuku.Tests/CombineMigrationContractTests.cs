using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Jellyfin.Plugin.Danmuku.Tests.StorageTestSql;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class CombineMigrationContractTests
{
    [Fact]
    public void UpgradePreservesV4DataAndBackupAndRejectsOldProgram()
    {
        using var storage = new StorageTestContext();
        var old = SchemaMigrations.Default.Take(4).ToArray();
        storage.CreateMigrator(old).Migrate();
        using (var c = storage.Factory.CreateOpenConnection())
        {
            InsertFile(c, "source");
            InsertBinding(c, "video", "source");
            Execute(c, """
                INSERT INTO MediaState VALUES('video','source',0,7,'Exists',123);
                INSERT INTO MediaState VALUES('off',NULL,1,3,'Missing',124);
                INSERT INTO MediaState VALUES('empty',NULL,0,0,'Unchecked',NULL);
                INSERT INTO Comments(FileId,SourceOrdinal,TimeMs,Text,Color,Mode,FontSize)
                    VALUES('source',0,720000,'boundary',16777215,1,25);
                """);
            InsertBatchAndTask(c, "batch", "video", "task");
        }
        var originalPath = Path.Combine(storage.Paths.OriginalFilesPath, "source.xml");
        File.WriteAllText(originalPath, "<i><d p=\"720,1,25,16777215\">boundary</d></i>");
        var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalPath));
        var result = storage.CreateMigrator().Migrate();
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalPath)));
        Assert.Equal(4, result.FromVersion);
        Assert.Equal(5, result.ToVersion);
        using (var backup = OpenReadOnly(result.BackupPath!))
            Assert.Equal(4, ScalarLong(backup, "SELECT Version FROM SchemaVersion"));
        using (var c = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal("source", ScalarString(c, "SELECT ActiveFileId FROM MediaState WHERE MediaId='video'"));
            Assert.Equal(7, ScalarLong(c, "SELECT Version FROM MediaState WHERE MediaId='video'"));
            Assert.Equal(1, ScalarLong(c, "SELECT IsDeactivated FROM MediaState WHERE MediaId='off'"));
            Assert.Equal(0, ScalarLong(c, "SELECT IsDeactivated FROM MediaState WHERE MediaId='empty'"));
            Assert.Equal(3, ScalarLong(c, "SELECT COUNT(*) FROM MediaState WHERE ActivePlanId IS NULL"));
            Assert.Equal("boundary", ScalarString(c, "SELECT Text FROM Comments"));
            Assert.Equal("Processing", ScalarString(c, "SELECT Status FROM ImportTasks"));
            Assert.Equal(3, ScalarLong(c, "SELECT COUNT(*) FROM pragma_table_info('PlaybackRequests') WHERE name IN ('SourceKind','PlanId','PlanVersion')"));
            Assert.Empty(QueryNames(c, "PRAGMA foreign_key_check"));
        }
        Assert.Null(storage.CreateMigrator().Migrate().BackupPath);
        Assert.Throws<InvalidOperationException>(() => storage.CreateMigrator(old).Migrate());
    }

    [Fact]
    public void FailedV5RebuildRollsBackAndCanResume()
    {
        using var storage = new StorageTestContext();
        var old = SchemaMigrations.Default.Take(4).ToArray();
        storage.CreateMigrator(old).Migrate();
        using (var c = storage.Factory.CreateOpenConnection())
            Execute(c, "INSERT INTO MediaState VALUES('video',NULL,1,4,'Missing',123)");
        var migration = SchemaMigrations.Default[4];
        var broken = migration with { Sql = migration.Sql.Replace("DROP TABLE MediaState;", "DROP TABLE MediaState; INSERT INTO MissingTable VALUES(1);", StringComparison.Ordinal) };
        Assert.Throws<SqliteException>(() => storage.CreateMigrator([.. old, broken]).Migrate());
        using (var c = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(4, ScalarLong(c, "SELECT Version FROM SchemaVersion"));
            Assert.Equal(4, ScalarLong(c, "SELECT Version FROM MediaState"));
            Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM sqlite_master WHERE name='CombinePlans'"));
            Assert.Equal(1, ScalarLong(c, "PRAGMA foreign_keys"));
        }
        Assert.Equal(5, storage.CreateMigrator().Migrate().ToVersion);
    }

    [Fact]
    public void PlanConstraintsProtectOwnershipSelectionReferencesAndIntegerTimes()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var c = storage.Factory.CreateOpenConnection();
        InsertFile(c, "source");
        InsertBinding(c, "video", "source");
        Execute(c, """
            INSERT INTO CombinePlans VALUES('p','video','combine-1',1,0,0);
            INSERT INTO CombineSegments VALUES('p',0,'source',0,NULL,0);
            INSERT INTO MediaState(MediaId,ActivePlanId) VALUES('video','p');
            """);
        Assert.Throws<SqliteException>(() => Execute(c, "DELETE FROM Files WHERE FileId='source'"));
        Assert.Throws<SqliteException>(() => Execute(c, "UPDATE MediaState SET ActiveFileId='source'"));
        Assert.Throws<SqliteException>(() => Execute(c, "UPDATE MediaState SET IsDeactivated=1"));
        Assert.Throws<SqliteException>(() => Execute(c, "INSERT INTO MediaState(MediaId,ActivePlanId) VALUES('other','p')"));
        Assert.Throws<SqliteException>(() => Execute(c, "INSERT INTO CombinePlans VALUES('q','video','combine-1',1,0,0)"));
        foreach (var expression in new[] { "-1", "0.5", "9007199254740992" })
            Assert.Throws<SqliteException>(() => Execute(c, $"UPDATE CombineSegments SET SourceStartMs={expression}"));
        Assert.Throws<SqliteException>(() => Execute(c, "UPDATE CombineSegments SET SourceEndMs=0"));
        Execute(c, """
            UPDATE MediaState SET ActivePlanId=NULL,IsDeactivated=1;
            DELETE FROM CombinePlans;
            """);
        Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM CombineSegments"));
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM Files"));
        Assert.Empty(QueryNames(c, "PRAGMA foreign_key_check"));
    }
}
