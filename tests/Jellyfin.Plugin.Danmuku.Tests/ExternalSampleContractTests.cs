using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Danmuku.Tests;

/// <summary>Missing private fixtures are visibly skipped; incorrect fixture identity fails.</summary>
public sealed class ExternalSampleFactAttribute : FactAttribute
{
    public ExternalSampleFactAttribute(string filename)
    {
        var directory = Environment.GetEnvironmentVariable("DANMUKU_SAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            Skip = "未设置 DANMUKU_SAMPLE_DIR，外部样本核对未执行（不计为通过）";
        else if (!File.Exists(Path.Combine(directory, filename)))
            Skip = $"未找到样本 {filename}，外部样本核对未执行（不计为通过）";
    }
}

[Collection("Import boundaries")]
public sealed class ExternalSampleContractTests(ITestOutputHelper output)
{
    [ExternalSampleFact("BV1zCtq61Eoi.xml")]
    public async Task FirstXmlMatchesRecordedFacts()
    {
        var (result, comments) = await ParseAsync("BV1zCtq61Eoi.xml", "3827a5efb558ce2fd9398139d6a02d5d556104b2176931268f210faccdc945f6");
        Assert.Equal(1065, result.Statistics.TotalSourceEntries);
        Assert.Equal(1065, comments.Count);
        Assert.Equal(new[] { 707, 2, 356 }, ModeCounts(comments));
        Assert.Equal(1063, comments.Count(c => c.FontSize == 25));
        Assert.Equal(2, comments.Count(c => c.FontSize == 18));
        Assert.Equal(18, comments.Select(c => c.Color).Distinct().Count());
    }

    [ExternalSampleFact("BV1zCtq61Eoi.json")]
    public async Task FirstJsonMatchesRecordedFacts()
    {
        var (result, comments) = await ParseAsync("BV1zCtq61Eoi.json", "4f3f73b79f4da149b0bd57b522fc3f28e6c2049cd2ceeff1f271bac827862f7b");
        Assert.Equal(1065, result.Statistics.TotalSourceEntries);
        Assert.Equal(1065, comments.Count);
        Assert.Equal(0, result.Statistics.IdFallbackCount);
        Assert.Equal(1900, comments.Min(c => c.TimeMs));
        Assert.Equal(1498967, comments.Max(c => c.TimeMs));
        Assert.Equal(comments.OrderBy(c => c.TimeMs).Select(c => c.SourceOrdinal), comments.Select(c => c.SourceOrdinal));
        Assert.Equal(1065, comments.Select(c => c.SourceId).Distinct().Count());
        Assert.All(comments, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
        Assert.Equal(100, comments.Max(c => c.Text.EnumerateRunes().Count()));
        Assert.Equal(1019, comments.Count(c => c.SourceId != c.SourceNumericId));
        Assert.Equal(new[] { 707, 2, 356 }, ModeCounts(comments));
    }

    [ExternalSampleFact("BV1cSec6tEux.xml")]
    public async Task SecondXmlMatchesRecordedFacts()
    {
        var (result, comments) = await ParseAsync("BV1cSec6tEux.xml", "00937d67b6fed630bcd9ea4f5aeef84e75ef8313df70014e503b1765439b6cbd");
        Assert.Equal(4485, result.Statistics.TotalSourceEntries);
        Assert.Equal(4485, comments.Count);
        Assert.Equal(60, comments.Max(c => c.Text.EnumerateRunes().Count()));
        Assert.All(comments, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [ExternalSampleFact("BV1cSec6tEux.json")]
    public async Task SecondJsonMatchesRecordedFacts()
    {
        var (result, comments) = await ParseAsync("BV1cSec6tEux.json", "3088b2820de74eadd7701b664f2a7a8f43e3e54a37b05a791889d1a5adacc75c");
        Assert.Equal(4499, result.Statistics.TotalSourceEntries);
        Assert.Equal(2, result.Statistics.AbnormalByReason[CommentAbnormalReason.MissingTime]);
        var expected = new[] { 3634, 44, 821 };
        var actual = ModeCounts(comments);
        for (var index = 0; index < actual.Length; index++)
            Assert.InRange(actual[index], expected[index] - 2, expected[index]);
    }

    private static int[] ModeCounts(List<CommentRecord> comments) =>
        new[] { 1, 4, 5 }.Select(mode => comments.Count(c => c.Mode == mode)).ToArray();

    private async Task<(ParseOutcome Result, List<CommentRecord> Comments)> ParseAsync(string filename, string hash)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("DANMUKU_SAMPLE_DIR")!, filename);
        using var source = File.OpenRead(path);
        Assert.Equal(hash, Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant());
        source.Position = 0;
        using var data = new ImportBoundaryData();
        var comments = new List<CommentRecord>();
        var result = await new DanmukuFileParser().ParseAsync(source, data.StagedPath, (batch, _) =>
        {
            comments.AddRange(batch);
            return Task.CompletedTask;
        });
        Assert.True(result.Accepted, result.RejectDetail);
        Assert.Equal(hash, result.Statistics.ContentSha256);
        output.WriteLine($"{filename}: total={result.Statistics.TotalSourceEntries}, normal={result.Statistics.NormalEntries}, abnormal={result.Statistics.AbnormalEntries}, modes={string.Join(',', ModeCounts(comments))}, maxCodepoints={comments.Max(c => c.Text.EnumerateRunes().Count())}, timeRange={comments.Min(c => c.TimeMs)}..{comments.Max(c => c.TimeMs)}, idMismatch={comments.Count(c => c.SourceId != c.SourceNumericId)}, fallback={result.Statistics.IdFallbackCount}, defaults={result.Statistics.DefaultModeCount}/{result.Statistics.DefaultFontSizeCount}/{result.Statistics.DefaultColorCount}, optional={string.Join(',', result.Statistics.UnparsedOptionalFieldCounts.Select(p => $"{p.Key}:{p.Value}"))}");
        return (result, comments);
    }
}
