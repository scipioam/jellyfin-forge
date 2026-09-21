using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

[Collection("Import boundaries")]
public sealed class ImportPipelineBoundaryTests
{
    [Fact]
    public async Task MaximumSourceCountFlowsThroughDiskStagingAndPublicationWithoutDedupingComments()
    {
        await using var x = new ImportLifecycleTestContext();
        using var fixture = new ImportBoundaryData();
        fixture.WriteEntries(json: true, normal: 300000);
        var batch = await x.BatchAsync();
        using (var source = File.OpenRead(fixture.SourcePath))
            Assert.Equal("Accepted", (await x.Imports.ReceiveAsync(batch.BatchId, 0, source, source.Length)).Status);
        await x.Imports.ProcessPendingAsync();
        var result = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0];
        Assert.Equal("Completed", result.Status);
        Assert.Equal(300000, result.ImportedComments);
        Assert.Equal(300000, x.Scalar("SELECT COUNT(*) FROM Comments"));
        Assert.Equal(300000, x.Scalar("SELECT MAX(SourceOrdinal) FROM Comments"));
        Assert.Equal(1, x.Scalar("SELECT MIN(SourceOrdinal) FROM Comments"));
        Assert.Equal(0, x.Scalar("SELECT SUM(TemporaryBytes) FROM ImportSlots"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
        Assert.Single(Directory.GetFiles(x.Storage.Paths.OriginalFilesPath));
    }

    [Fact]
    public async Task ActualStreamByteLimitIsEnforcedWithoutTrustingDeclaredLength()
    {
        await using var x = new ImportLifecycleTestContext();
        using var fixture = new ImportBoundaryData();
        fixture.WriteByteBoundary(json: true, bytes: 52428801);
        var batch = await x.BatchAsync();
        using var source = File.OpenRead(fixture.SourcePath);
        var result = await x.Imports.ReceiveAsync(batch.BatchId, 0, source);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("FileTooLarge", result.ErrorCode);
        Assert.Null(result.TaskId);
        Assert.Equal(0, x.Scalar("SELECT SUM(TemporaryBytes) FROM ImportSlots"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }
}
