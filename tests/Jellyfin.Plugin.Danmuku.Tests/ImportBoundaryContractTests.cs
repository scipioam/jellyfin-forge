using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Model;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

[CollectionDefinition("Import boundaries", DisableParallelization = true)]
public sealed class ImportBoundaryCollection;

[Collection("Import boundaries")]
public sealed class ImportBoundaryContractTests
{
    [Theory]
    [InlineData(false, 300000, 0)]
    [InlineData(true, 300000, 0)]
    [InlineData(false, 300001, 0)]
    [InlineData(true, 300001, 0)]
    [InlineData(false, 299999, 2)]
    [InlineData(true, 299999, 2)]
    [InlineData(false, 0, 2001)]
    [InlineData(true, 0, 2001)]
    public async Task ProductionEntryLimitsCountNormalAndAbnormalEntries(bool json, int normal, int abnormal)
    {
        using var data = new ImportBoundaryData();
        data.WriteEntries(json, normal, abnormal);
        Assert.True(new FileInfo(data.SourcePath).Length < 52428800);
        var (outcome, emitted) = await ParseAsync(data);
        Assert.Equal(normal + abnormal, outcome.Statistics.TotalSourceEntries);
        if (normal + abnormal > 300000)
        {
            Assert.False(outcome.Accepted);
            Assert.Equal(ParseRejectReason.EntryCountExceeded, outcome.RejectReason);
            // Callback batches are provisional, not published output. P4 owns their rollback.
            Assert.True(emitted <= 300000);
        }
        else if (normal == 0)
        {
            Assert.False(outcome.Accepted);
            Assert.Equal(ParseRejectReason.ZeroValidEntries, outcome.RejectReason);
            Assert.Equal(abnormal, outcome.Statistics.AbnormalByReason[CommentAbnormalReason.MissingTime]);
            Assert.Equal(0, emitted);
        }
        else
        {
            Assert.True(outcome.Accepted, outcome.RejectDetail);
            Assert.Equal(300000, emitted);
            Assert.Equal(300000, outcome.Statistics.NormalEntries);
            Assert.Equal(0, outcome.Statistics.AbnormalEntries);
            await AssertHashAsync(data, outcome);
        }
    }

    [Theory]
    [InlineData(false, 52428800L)]
    [InlineData(true, 52428800L)]
    [InlineData(false, 52428801L)]
    [InlineData(true, 52428801L)]
    public async Task ProductionByteLimitIsIndependentOfEntryLimit(bool json, long bytes)
    {
        using var data = new ImportBoundaryData();
        data.WriteByteBoundary(json, bytes);
        var (outcome, _) = await ParseAsync(data);
        Assert.InRange(outcome.Statistics.TotalSourceEntries, 1, 299999);
        Assert.True(new FileInfo(data.StagedPath).Length <= 52428800);
        if (bytes == 52428800)
        {
            Assert.True(outcome.Accepted, outcome.RejectDetail);
            Assert.True(outcome.RequiresConfirmation);
            Assert.Equal(1, outcome.Statistics.NormalEntries);
            Assert.Equal(outcome.Statistics.AbnormalEntries, outcome.Statistics.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
            await AssertHashAsync(data, outcome);
        }
        else
        {
            Assert.False(outcome.Accepted);
            Assert.Equal(ParseRejectReason.FileTooLarge, outcome.RejectReason);
            Assert.Null(outcome.Statistics.ContentSha256);
        }
    }

    [Fact]
    public async Task JsonTokensCrossProductionBuffersAndOversizedEntriesDoNotHideFollowingEntries()
    {
        using var data = new ImportBoundaryData();
        using (var writer = data.CreateWriter())
        {
            writer.Write("[{\"progress\":1,\"content\":\"synthetic\",\"unknown\":\"");
            ImportBoundaryData.WriteRepeated(writer, 'x', 131072);
            writer.Write("\"},{\"progress\":2,\"content\":\"");
            ImportBoundaryData.WriteRepeated(writer, 'x', 131072);
            writer.Write("\"},{\"progress\":3,\"content\":\"synthetic\",\"unknown\":\"");
            ImportBoundaryData.WriteRepeated(writer, 'x', 4194304);
            writer.Write("\"},{\"progress\":4,\"content\":\"synthetic\"}]");
        }

        var (outcome, emitted) = await ParseAsync(data);
        Assert.True(outcome.Accepted, outcome.RejectDetail);
        Assert.Equal(4, outcome.Statistics.TotalSourceEntries);
        Assert.Equal(2, emitted);
        Assert.Equal(1, outcome.Statistics.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
        Assert.Equal(1, outcome.Statistics.AbnormalByReason[CommentAbnormalReason.EntryTooLarge]);
        await AssertHashAsync(data, outcome);
    }

    [Theory]
    [InlineData("\"unknown\" \"", "\"}")]
    [InlineData("\"unknown\":\"", "\\q\"}")]
    [InlineData("\"unknown\":\"", "\",\"value\":01}")]
    [InlineData("\"unknown\":\"", "\",}")]
    [InlineData("\"unknown\":\"", "\",\"value\":tru}")]
    public async Task OversizedJsonStillRequiresValidGrammar(string prefix, string suffix)
    {
        using var data = new ImportBoundaryData();
        using (var writer = data.CreateWriter())
        {
            writer.Write("[{" + prefix);
            ImportBoundaryData.WriteRepeated(writer, 'x', 4194304);
            writer.Write(suffix + ",{\"progress\":1,\"content\":\"synthetic\"}]");
        }

        var (outcome, _) = await ParseAsync(data);
        Assert.False(outcome.Accepted);
        Assert.Equal(ParseRejectReason.StructureInvalid, outcome.RejectReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XmlTextAndCdataUseBoundedChunks(bool cdata)
    {
        using var data = new ImportBoundaryData();
        using (var writer = data.CreateWriter())
        {
            writer.Write("<i>");
            foreach (var count in new[] { 100, 101, 131072 })
            {
                writer.Write("<d p=\"1\">");
                if (cdata) writer.Write("<![CDATA[");
                ImportBoundaryData.WriteRepeated(writer, 'x', count);
                if (cdata) writer.Write("]]>");
                writer.Write("</d>");
            }

            writer.Write("</i>");
        }

        var (outcome, emitted) = await ParseAsync(data);
        Assert.True(outcome.Accepted, outcome.RejectDetail);
        Assert.Equal(1, emitted);
        Assert.Equal(2, outcome.Statistics.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
    }

    private static async Task<(ParseOutcome Outcome, long Emitted)> ParseAsync(ImportBoundaryData data)
    {
        using var source = File.OpenRead(data.SourcePath);
        long emitted = 0;
        long ordinal = 0;
        var outcome = await new DanmukuFileParser().ParseAsync(source, data.StagedPath, (batch, _) =>
        {
            Assert.InRange(batch.Count, 1, 1000);
            foreach (var comment in batch)
            {
                Assert.True(comment.SourceOrdinal > ordinal);
                ordinal = comment.SourceOrdinal;
            }

            emitted += batch.Count;
            return Task.CompletedTask;
        }, (errors, _) =>
        {
            Assert.InRange(errors.Count, 1, 1000);
            Assert.All(errors, error => Assert.True(error.TextSummary is null || error.TextSummary.EnumerateRunes().Count() <= 201));
            return Task.CompletedTask;
        });
        return (outcome, emitted);
    }

    private static async Task AssertHashAsync(ImportBoundaryData data, ParseOutcome outcome)
    {
        using var source = File.OpenRead(data.SourcePath);
        using var staged = File.OpenRead(data.StagedPath);
        var expected = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
        Assert.Equal(expected, outcome.Statistics.ContentSha256);
        Assert.Equal(expected, Convert.ToHexString(await SHA256.HashDataAsync(staged)).ToLowerInvariant());
    }
}
