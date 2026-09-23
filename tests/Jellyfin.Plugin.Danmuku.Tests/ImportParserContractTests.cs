using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class ImportParserContractTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task XmlParserCountsEntriesAndMapsFieldsAndSourceOrdinals()
    {
        var content = Xml(
            D("1.5,1,25,16777215,1700000000,0,sender-a,10001", "测试弹幕0001"),
            D("2.25,4,18,16711680,1700000001,1,sender-b,10002", "测试弹幕0002"),
            D("3,5,25,65280,1700000002,2,sender-c,10003", "测试弹幕0003"));

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(DanmukuFormat.Xml, run.Outcome.Format);
        Assert.False(run.Outcome.RequiresConfirmation);

        var stats = run.Outcome.Statistics;
        Assert.Equal(3L, stats.TotalSourceEntries);
        Assert.Equal(3L, stats.NormalEntries);
        Assert.Equal(0L, stats.AbnormalEntries);
        Assert.Equal(0L, stats.DefaultModeCount);
        Assert.Equal(0L, stats.DefaultFontSizeCount);
        Assert.Equal(0L, stats.DefaultColorCount);
        Assert.Equal(0L, stats.IdFallbackCount);
        Assert.Empty(stats.AbnormalByReason);
        Assert.Empty(run.Errors);

        Assert.Equal(new long[] { 1, 2, 3 }, run.Comments.Select(comment => comment.SourceOrdinal));
        var first = run.Comments[0];
        Assert.Equal(1500L, first.TimeMs);
        Assert.Equal(1, first.Mode);
        Assert.Equal(25, first.FontSize);
        Assert.Equal(16777215L, first.Color);
        Assert.Equal(1_700_000_000_000L, first.SourceTimeMs);
        Assert.Equal(0L, first.Pool);
        Assert.Equal("sender-a", first.SenderHash);
        Assert.Equal("10001", first.SourceId);
        Assert.Equal("测试弹幕0001", first.Text);
        Assert.Null(first.SourceNumericId);
        Assert.Null(first.Weight);
        Assert.Null(first.Attr);

        Assert.Equal(4, run.Comments[1].Mode);
        Assert.Equal(18, run.Comments[1].FontSize);
        Assert.Equal(5, run.Comments[2].Mode);
        Assert.Equal(16777215L, first.Color);

        // The staged copy is byte-identical to the input.
        Assert.Equal(run.Input, run.Staged);
    }

    [Theory]
    [InlineData("1.2345", 1235L)]
    [InlineData("1.2344", 1234L)]
    [InlineData("0.0005", 1L)]
    [InlineData("0.0015", 2L)]
    [InlineData("0.0025", 3L)]
    [InlineData("2.5", 2500L)]
    [InlineData("0", 0L)]
    [InlineData("1e3", 1000000L)]
    public async Task XmlSecondsConvertToMillisecondsWithHalfAwayFromZeroRounding(string seconds, long expected)
    {
        var content = Xml(D($"{seconds},1,25,16777215", "测试弹幕0001"));

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(expected, Assert.Single(run.Comments).TimeMs);
    }

    [Fact]
    public async Task XmlSourceTimeUsesTheSameSecondsConversion()
    {
        var content = Xml(D("1.2345,1,25,16777215,2.5005,0,sender-a,1", "测试弹幕0001"));

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(2501L, Assert.Single(run.Comments).SourceTimeMs);
    }

    [Fact]
    public async Task XmlAcceptsMissingTrailingFieldsAndKeepsCommaPlaceholders()
    {
        var content = Xml(
            D("2.5,4,25,16777215", "测试弹幕0001"),
            D("3.5,1", "测试弹幕0002"),
            D("4.5,,25,16777215", "测试弹幕0003"),
            D("5.5,,,,,,,999", "测试弹幕0004"));

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        var stats = run.Outcome.Statistics;
        Assert.Equal(4L, stats.NormalEntries);
        Assert.Equal(0L, stats.AbnormalEntries);
        Assert.Equal(2L, stats.DefaultModeCount);
        Assert.Equal(2L, stats.DefaultFontSizeCount);
        Assert.Equal(2L, stats.DefaultColorCount);
        Assert.Empty(run.Errors);

        Assert.Equal(4, run.Comments[0].Mode);
        Assert.Equal(25, run.Comments[0].FontSize);
        Assert.Null(run.Comments[0].SourceId);

        Assert.Equal(1, run.Comments[1].Mode);
        Assert.Equal(DanmukuImportLimits.DefaultFontSize, run.Comments[1].FontSize);
        Assert.Equal(DanmukuImportLimits.DefaultColor, run.Comments[1].Color);

        Assert.Equal(DanmukuImportLimits.DefaultMode, run.Comments[2].Mode);
        Assert.Equal(25, run.Comments[2].FontSize);
        Assert.Equal(16777215L, run.Comments[2].Color);

        Assert.Equal(DanmukuImportLimits.DefaultMode, run.Comments[3].Mode);
        Assert.Equal(DanmukuImportLimits.DefaultFontSize, run.Comments[3].FontSize);
        Assert.Equal(DanmukuImportLimits.DefaultColor, run.Comments[3].Color);
        Assert.Equal("999", run.Comments[3].SourceId);
        Assert.Null(run.Comments[3].SenderHash);
        Assert.Null(run.Comments[3].Pool);
        Assert.Null(run.Comments[3].SourceTimeMs);
    }

    [Theory]
    [InlineData("<noti><d p=\"1,1,25,16777215\">测试弹幕0001</d></noti>")]
    [InlineData("<i><metadata>value</metadata></i>")]
    [InlineData("<i><d>测试弹幕0001</d></i>")]
    [InlineData("<i></i>")]
    [InlineData("plain text, not xml")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE i [<!ENTITY x \"y\">]><i><d p=\"1,1,25,16777215\">测试弹幕0001</d></i>")]
    [InlineData("<i><d p=\"1,1,25,16777215\">unclosed")]
    [InlineData("[{\"progress\":1,\"content\":\"测试弹幕0001\"}")]
    [InlineData("[123]")]
    [InlineData("{\"progress\":1,\"content\":\"测试弹幕0001\"}")]
    [InlineData("[{\"progress\":1,\"content\":\"测试弹幕0001\"}] trailing")]
    [InlineData("[{\"progress\":1,\"content\":\"测试弹幕0001\"},]")]
    public async Task StructureFailuresAreRejectedWithoutEmittingBatches(string content)
    {
        var run = await ParseAsync(content);

        Assert.False(run.Outcome.Accepted);
        Assert.Equal(ParseRejectReason.StructureInvalid, run.Outcome.RejectReason);
        Assert.False(string.IsNullOrWhiteSpace(run.Outcome.RejectDetail));
        Assert.Empty(run.Comments);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task JsonPrefersIdStrAndFallsBackToRawIntegerIdText()
    {
        var content = "[" +
            "{\"progress\":1000,\"content\":\"测试弹幕0001\",\"idStr\":\"id-a\",\"id\":123,\"weight\":10,\"attr\":5," +
            "\"unknown\":{\"nested\":[1,2,{\"deep\":true}]}}," +
            "{\"progress\":2000,\"content\":\"测试弹幕0002\",\"id\":9007199254740993}," +
            "{\"progress\":3000,\"content\":\"测试弹幕0003\"}," +
            "{\"progress\":4000,\"content\":\"测试弹幕0004\",\"idStr\":\"\",\"id\":456}]";

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        var stats = run.Outcome.Statistics;
        Assert.Equal(4L, stats.NormalEntries);
        Assert.Equal(0L, stats.AbnormalEntries);
        Assert.Equal(2L, stats.IdFallbackCount);
        Assert.Empty(stats.UnparsedOptionalFieldCounts);

        Assert.Equal("id-a", run.Comments[0].SourceId);
        Assert.Equal("123", run.Comments[0].SourceNumericId);
        Assert.Equal(10L, run.Comments[0].Weight);
        Assert.Equal(5L, run.Comments[0].Attr);

        // The raw digit text is preserved even beyond the floating point precision range.
        Assert.Equal("9007199254740993", run.Comments[1].SourceId);
        Assert.Equal("9007199254740993", run.Comments[1].SourceNumericId);

        Assert.Null(run.Comments[2].SourceId);
        Assert.Null(run.Comments[2].SourceNumericId);

        Assert.Equal("456", run.Comments[3].SourceId);
        Assert.Equal("456", run.Comments[3].SourceNumericId);
    }

    [Fact]
    public async Task JsonMissingOrNonIntegerProgressIsAnAbnormalEntry()
    {
        var content = "[" +
            "{\"content\":\"测试弹幕0001\"}," +
            "{\"progress\":12.5,\"content\":\"测试弹幕0002\"}," +
            "{\"progress\":\"100\",\"content\":\"测试弹幕0003\"}," +
            "{\"progress\":null,\"content\":\"测试弹幕0004\"}," +
            "{\"progress\":-5,\"content\":\"测试弹幕0005\"}," +
            "{\"progress\":99999999999999999999,\"content\":\"测试弹幕0006\"}," +
            "{\"progress\":5000,\"content\":\"测试弹幕0007\"}]";

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        Assert.True(run.Outcome.RequiresConfirmation);
        var stats = run.Outcome.Statistics;
        Assert.Equal(7L, stats.TotalSourceEntries);
        Assert.Equal(1L, stats.NormalEntries);
        Assert.Equal(6L, stats.AbnormalEntries);
        Assert.Equal(1L, stats.AbnormalByReason[CommentAbnormalReason.MissingTime]);
        Assert.Equal(5L, stats.AbnormalByReason[CommentAbnormalReason.InvalidTime]);

        Assert.Equal(6, run.Errors.Count);
        Assert.Equal("MissingTime", run.Errors[0].ReasonCodes);
        Assert.Equal(1L, run.Errors[0].SourceOrdinal);
        Assert.Equal(2L, run.Errors[1].SourceOrdinal);
        Assert.Equal(5000L, Assert.Single(run.Comments).TimeMs);
    }

    [Fact]
    public async Task JsonOptionalMetadataIsDroppedAndCountedWithoutBlocking()
    {
        var content = "[" +
            "{\"progress\":1,\"content\":\"测试弹幕0001\",\"ctime\":\"not-a-number\",\"midHash\":42,\"weight\":1.5,\"attr\":{},\"id\":1.5}," +
            "{\"progress\":2,\"content\":\"测试弹幕0002\",\"ctime\":1,\"midHash\":\"hash-b\",\"weight\":7,\"attr\":3,\"id\":10}]";

        var run = await ParseAsync(content);

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(2L, run.Outcome.Statistics.NormalEntries);
        Assert.Equal(0L, run.Outcome.Statistics.AbnormalEntries);
        var dropped = run.Outcome.Statistics.UnparsedOptionalFieldCounts;
        Assert.Equal(1L, dropped["ctime"]);
        Assert.Equal(1L, dropped["midHash"]);
        Assert.Equal(1L, dropped["weight"]);
        Assert.Equal(1L, dropped["attr"]);
        Assert.Equal(1L, dropped["id"]);

        Assert.Null(run.Comments[0].SourceId);
        Assert.Null(run.Comments[0].SourceTimeMs);
        Assert.Null(run.Comments[0].SenderHash);
        Assert.Null(run.Comments[0].Weight);
        Assert.Null(run.Comments[0].Attr);

        Assert.Equal("10", run.Comments[1].SourceId);
        Assert.Equal(1L, run.Comments[1].SourceTimeMs);
        Assert.Equal("hash-b", run.Comments[1].SenderHash);
        Assert.Equal(7L, run.Comments[1].Weight);
        Assert.Equal(3L, run.Comments[1].Attr);
        Assert.Equal(1L, run.Outcome.Statistics.IdFallbackCount);
    }

    [Fact]
    public async Task JsonTextCodepointBoundariesFollowUnicodeCodePoints()
    {
        var exactly100 = new string('a', 100);
        var over100 = new string('a', 101);
        var emoji100 = string.Concat(Enumerable.Repeat("😀", 100));
        var emoji101 = string.Concat(Enumerable.Repeat("😀", 101));
        var combining50 = string.Concat(Enumerable.Repeat("e\u0301", 50));
        var combining100 = string.Concat(Enumerable.Repeat("e\u0301", 100));

        var content = "[" +
            $"{{\"progress\":1,\"content\":\"{exactly100}\"}}," +
            $"{{\"progress\":2,\"content\":\"{over100}\"}}," +
            $"{{\"progress\":3,\"content\":\"{emoji100}\"}}," +
            $"{{\"progress\":4,\"content\":\"{emoji101}\"}}," +
            $"{{\"progress\":5,\"content\":\"{combining50}\"}}," +
            $"{{\"progress\":6,\"content\":\"{combining100}\"}}," +
            "{\"progress\":7,\"content\":\"   \"}," +
            "{\"progress\":8,\"content\":null}," +
            "{\"progress\":9,\"content\":\"\"}]";

        var run = await ParseAsync(content);

        var stats = run.Outcome.Statistics;
        Assert.Equal(9L, stats.TotalSourceEntries);
        Assert.Equal(3L, stats.NormalEntries);
        Assert.Equal(6L, stats.AbnormalEntries);
        Assert.Equal(3L, stats.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
        Assert.Equal(3L, stats.AbnormalByReason[CommentAbnormalReason.BlankText]);
        Assert.Equal(exactly100, run.Comments[0].Text);
        Assert.Equal(emoji100, run.Comments[1].Text);
        Assert.Equal(combining50, run.Comments[2].Text);
    }

    [Fact]
    public async Task XmlTextCodepointBoundariesAndBoundedSummaries()
    {
        var content = Xml(
            D("1,1,25,16777215", new string('汉', 100)),
            D("2,1,25,16777215", new string('汉', 101)),
            D("3,1,25,16777215", string.Concat(Enumerable.Repeat("😀", 100))),
            D("4,1,25,16777215", string.Concat(Enumerable.Repeat("😀", 101))),
            D("5,1,25,16777215", "   "),
            D("6,1,25,16777215", new string('测', 300)));

        var run = await ParseAsync(content);

        var stats = run.Outcome.Statistics;
        Assert.Equal(6L, stats.TotalSourceEntries);
        Assert.Equal(2L, stats.NormalEntries);
        Assert.Equal(4L, stats.AbnormalEntries);
        Assert.Equal(3L, stats.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
        Assert.Equal(1L, stats.AbnormalByReason[CommentAbnormalReason.BlankText]);
        Assert.Equal(new string('汉', 100), run.Comments[0].Text);
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 100)), run.Comments[1].Text);

        // Entries 2, 4, 5 and 6 are abnormal in that order; the 300-code-point summary is capped.
        var longSummary = run.Errors[3].TextSummary!;
        Assert.Equal(201, longSummary.Length);
        Assert.EndsWith("\u2026", longSummary, StringComparison.Ordinal);
        Assert.All(run.Errors, error => Assert.True(error.TextSummary is null || error.TextSummary.Length <= 401));
        Assert.All(run.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.ReasonCodes)));
    }

    [Fact]
    public async Task ModeFontSizeAndColorDefaultsAndValidationAreReported()
    {
        var content = "[" +
            "{\"progress\":1,\"content\":\"测试弹幕0001\"}," +
            "{\"progress\":2,\"content\":\"测试弹幕0002\",\"mode\":6}," +
            "{\"progress\":3,\"content\":\"测试弹幕0003\",\"mode\":0}," +
            "{\"progress\":4,\"content\":\"测试弹幕0004\",\"mode\":\"4\"}," +
            "{\"progress\":5,\"content\":\"测试弹幕0005\",\"mode\":4.0}," +
            "{\"progress\":6,\"content\":\"测试弹幕0006\",\"fontsize\":0}," +
            "{\"progress\":7,\"content\":\"测试弹幕0007\",\"fontsize\":25.5}," +
            "{\"progress\":8,\"content\":\"测试弹幕0008\",\"color\":16777216}," +
            "{\"progress\":9,\"content\":\"测试弹幕0009\",\"color\":-1}," +
            "{\"progress\":10,\"content\":\"测试弹幕0010\",\"mode\":4,\"fontsize\":18,\"color\":0}]";

        var run = await ParseAsync(content);

        var stats = run.Outcome.Statistics;
        Assert.Equal(10L, stats.TotalSourceEntries);
        Assert.Equal(2L, stats.NormalEntries);
        Assert.Equal(8L, stats.AbnormalEntries);
        // Entries 1/6/7/8/9 omit mode, entries 1-5/8/9 omit fontsize, entries 1-7 omit color.
        Assert.Equal(5L, stats.DefaultModeCount);
        Assert.Equal(7L, stats.DefaultFontSizeCount);
        Assert.Equal(7L, stats.DefaultColorCount);
        Assert.Equal(1L, stats.UnsupportedModeCount);
        Assert.Equal(1L, stats.UnsupportedModeDistribution[6]);
        Assert.Equal(1L, stats.AbnormalByReason[CommentAbnormalReason.UnsupportedMode]);
        Assert.Equal(3L, stats.AbnormalByReason[CommentAbnormalReason.InvalidMode]);
        Assert.Equal(2L, stats.AbnormalByReason[CommentAbnormalReason.InvalidFontSize]);
        Assert.Equal(2L, stats.AbnormalByReason[CommentAbnormalReason.InvalidColor]);

        Assert.Equal(DanmukuImportLimits.DefaultMode, run.Comments[0].Mode);
        Assert.Equal(DanmukuImportLimits.DefaultFontSize, run.Comments[0].FontSize);
        Assert.Equal(DanmukuImportLimits.DefaultColor, run.Comments[0].Color);
        Assert.Equal(4, run.Comments[1].Mode);
        Assert.Equal(18, run.Comments[1].FontSize);
        Assert.Equal(0L, run.Comments[1].Color);
    }

    [Fact]
    public async Task XmlModeFontSizeAndColorValidationIsReported()
    {
        var content = Xml(
            D("1,6,25,16777215", "测试弹幕0001"),
            D("2,x,25,16777215", "测试弹幕0002"),
            D("3,1,0,16777215", "测试弹幕0003"),
            D("4,1,25,16777216", "测试弹幕0004"),
            D("5,1,25,16777215", "测试弹幕0005"));

        var run = await ParseAsync(content);

        var stats = run.Outcome.Statistics;
        Assert.Equal(5L, stats.TotalSourceEntries);
        Assert.Equal(1L, stats.NormalEntries);
        Assert.Equal(4L, stats.AbnormalEntries);
        Assert.Equal(1L, stats.UnsupportedModeCount);
        Assert.Equal(1L, stats.UnsupportedModeDistribution[6]);
        Assert.Equal(1L, stats.AbnormalByReason[CommentAbnormalReason.InvalidMode]);
        Assert.Equal(1L, stats.AbnormalByReason[CommentAbnormalReason.InvalidFontSize]);
        Assert.Equal(1L, stats.AbnormalByReason[CommentAbnormalReason.InvalidColor]);
    }

    [Fact]
    public async Task FilesWithOnlyAbnormalEntriesAreRejectedAsZeroValidEntries()
    {
        var json = "[{\"content\":\"测试弹幕0001\"},{\"content\":\"测试弹幕0002\",\"progress\":1.5}]";
        var jsonRun = await ParseAsync(json);

        Assert.False(jsonRun.Outcome.Accepted);
        Assert.Equal(ParseRejectReason.ZeroValidEntries, jsonRun.Outcome.RejectReason);
        Assert.Empty(jsonRun.Comments);
        Assert.Equal(2L, jsonRun.Outcome.Statistics.AbnormalEntries);

        var xml = Xml(D("", "   "), D("1,1,25,16777215", "   "));
        var xmlRun = await ParseAsync(xml);

        Assert.False(xmlRun.Outcome.Accepted);
        Assert.Equal(ParseRejectReason.ZeroValidEntries, xmlRun.Outcome.RejectReason);
        Assert.Empty(xmlRun.Comments);
        Assert.Equal(2L, xmlRun.Outcome.Statistics.AbnormalEntries);
    }

    [Fact]
    public async Task StagedCopyHashAndRepeatedRunsAreIdentical()
    {
        var content = Xml(
            D("1.5,1,25,16777215,1700000000,0,sender-a,1", "测试弹幕0001"),
            D("2.5,4,18,255,1700000001,1,sender-b,2", "测试弹幕0002"));
        var bytes = Utf8NoBom.GetBytes(content);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var first = await ParseAsync(content, stagedName: "first.dat");
        var second = await ParseAsync(content, stagedName: "second.dat");

        Assert.Equal(bytes, first.Staged);
        Assert.Equal(bytes, second.Staged);
        Assert.Equal(expectedHash, first.Outcome.Statistics.ContentSha256);
        Assert.Equal(expectedHash, second.Outcome.Statistics.ContentSha256);
        Assert.EndsWith("first.dat", first.Outcome.Statistics.StagedPath!, StringComparison.Ordinal);

        // Two runs over the same input produce identical records and counters.
        Assert.Equal(first.Comments, second.Comments);
        Assert.Equal(DescribeStatistics(first.Outcome.Statistics), DescribeStatistics(second.Outcome.Statistics));
    }

    [Fact]
    public async Task FileByteLimitAbortsWhileReadingAndKeepsStagedCopyBounded()
    {
        var content = Xml(D("1,1,25,16777215", "测试弹幕0001"));
        Assert.True(Utf8NoBom.GetByteCount(content) > 100);

        var run = await ParseAsync(content, new DanmukuParserLimits { MaxFileBytes = 100 });

        Assert.False(run.Outcome.Accepted);
        Assert.Equal(ParseRejectReason.FileTooLarge, run.Outcome.RejectReason);
        Assert.Empty(run.Comments);
        Assert.Empty(run.Errors);
        Assert.True(run.Staged.Length <= 100, $"staged length was {run.Staged.Length}");
    }

    [Fact]
    public async Task FileByteLimitAcceptsAFileOfExactlyTheLimit()
    {
        var baseContent = "<i>" + D("1,1,25,16777215", "测试弹幕0001") + "</i>";
        var baseBytes = Utf8NoBom.GetBytes(baseContent);
        Assert.True(baseBytes.Length < 100);
        var content = baseContent + new string(' ', 100 - baseBytes.Length);
        var bytes = Utf8NoBom.GetBytes(content);
        Assert.Equal(100, bytes.Length);

        var run = await ParseBytesAsync(bytes, new DanmukuParserLimits { MaxFileBytes = 100 });

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(1L, run.Outcome.Statistics.NormalEntries);
    }

    [Fact]
    public async Task EntryLimitAbortsBeforeEmittingAPartialBatch()
    {
        var entries = Enumerable.Range(1, 6)
            .Select(index => D($"{index},1,25,16777215", $"测试弹幕{index:0000}"))
            .ToArray();

        var run = await ParseAsync(Xml(entries), new DanmukuParserLimits { MaxSourceEntries = 5 });

        Assert.False(run.Outcome.Accepted);
        Assert.Equal(ParseRejectReason.EntryCountExceeded, run.Outcome.RejectReason);
        Assert.Empty(run.Comments);
        Assert.Empty(run.Errors);

        var allowed = await ParseAsync(Xml(entries[..5]), new DanmukuParserLimits { MaxSourceEntries = 5 });
        Assert.True(allowed.Outcome.Accepted);
        Assert.Equal(5L, allowed.Outcome.Statistics.NormalEntries);
    }

    [Fact]
    public async Task JsonEntryLimitIncludesAbnormalEntries()
    {
        var content = "[" +
            "{\"progress\":1,\"content\":\"测试弹幕0001\"}," +
            "{\"content\":\"测试弹幕0002\"}," +
            "{\"progress\":3,\"content\":\"测试弹幕0003\"}]";

        var run = await ParseAsync(content, new DanmukuParserLimits { MaxSourceEntries = 2 });

        Assert.False(run.Outcome.Accepted);
        Assert.Equal(ParseRejectReason.EntryCountExceeded, run.Outcome.RejectReason);
        Assert.Empty(run.Comments);
    }

    [Fact]
    public async Task SmallInitialBuffersDoNotBreakTokensSpanningBuffers()
    {
        var longText = string.Concat(Enumerable.Repeat("测", 150));
        var content = "[" +
            "{\"progress\":100,\"content\":\"" + longText + "\",\"unknown\":\"" + new string('x', 200) + "\"}," +
            "{\"progress\":200,\"content\":\"测试弹幕0001\",\"id\":9007199254740993}]";

        var run = await ParseAsync(content, new DanmukuParserLimits { InitialBufferSize = 16 });

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(2L, run.Outcome.Statistics.TotalSourceEntries);
        Assert.Equal(1L, run.Outcome.Statistics.NormalEntries);
        Assert.Equal(1L, run.Outcome.Statistics.AbnormalEntries);
        Assert.Equal(1L, run.Outcome.Statistics.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
        Assert.Equal("测试弹幕0001", Assert.Single(run.Comments).Text);
        Assert.Equal("9007199254740993", run.Comments[0].SourceId);
    }

    [Fact]
    public async Task XmlLongTextWithTinySniffBufferIsAnAbnormalEntryInsteadOfAStructureFailure()
    {
        var longText = string.Concat(Enumerable.Repeat("测", 150));
        var content = Xml(
            D("1,1,25,16777215", longText),
            D("2,1,25,16777215", "测试弹幕0001"));

        var run = await ParseAsync(content, new DanmukuParserLimits { InitialBufferSize = 16 });

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(2L, run.Outcome.Statistics.TotalSourceEntries);
        Assert.Equal(1L, run.Outcome.Statistics.NormalEntries);
        Assert.Equal(1L, run.Outcome.Statistics.AbnormalByReason[CommentAbnormalReason.TextTooLong]);
        Assert.Equal("测试弹幕0001", Assert.Single(run.Comments).Text);
    }

    [Fact]
    public async Task JsonElementBeyondTheBufferCapIsAnAbnormalEntryAndParsingContinues()
    {
        var content = "[" +
            "{\"progress\":100,\"content\":\"测试弹幕0001\",\"extra\":\"" + new string('x', 300) + "\"}," +
            "{\"progress\":200,\"content\":\"测试弹幕0002\"}]";

        var run = await ParseAsync(content, new DanmukuParserLimits
        {
            InitialBufferSize = 16,
            MaxJsonElementBytes = 64
        });

        Assert.True(run.Outcome.Accepted);
        Assert.Equal(2L, run.Outcome.Statistics.TotalSourceEntries);
        Assert.Equal(1L, run.Outcome.Statistics.NormalEntries);
        Assert.Equal(1L, run.Outcome.Statistics.AbnormalEntries);
        Assert.Equal(1L, run.Outcome.Statistics.AbnormalByReason[CommentAbnormalReason.EntryTooLarge]);
        Assert.Equal("测试弹幕0002", Assert.Single(run.Comments).Text);
    }

    [Fact]
    public async Task BatchCallbacksSplitLargeFilesIntoBoundedBatches()
    {
        var entries = Enumerable.Range(1, 7)
            .Select(index => "{\"progress\":" + index + ",\"content\":\"测试弹幕" + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "\"}")
            .ToArray();
        var content = "[" + string.Join(',', entries) + "]";

        using var storage = new StorageTestContext();
        var stagedPath = Path.Combine(storage.Paths.StagingPath, "batched.dat");
        var parser = new DanmukuFileParser(new DanmukuParserLimits { CommentBatchSize = 3 });
        var batchSizes = new List<int>();
        using var source = new MemoryStream(Utf8NoBom.GetBytes(content), writable: false);

        var outcome = await parser.ParseAsync(
            source,
            stagedPath,
            (batch, _) =>
            {
                batchSizes.Add(batch.Count);
                return Task.CompletedTask;
            });

        Assert.True(outcome.Accepted);
        Assert.Equal(new[] { 3, 3, 1 }, batchSizes);
    }

    private static string DescribeStatistics(ParseStatistics stats) =>
        string.Join(
            "|",
            stats.TotalSourceEntries,
            stats.NormalEntries,
            stats.AbnormalEntries,
            string.Join(",", stats.AbnormalByReason.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")),
            stats.DefaultModeCount,
            stats.DefaultFontSizeCount,
            stats.DefaultColorCount,
            stats.IdFallbackCount,
            string.Join(",", stats.UnsupportedModeDistribution.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")),
            string.Join(",", stats.UnparsedOptionalFieldCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")),
            stats.ContentSha256);

    private static string Xml(params string[] entries) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><i><chatserver>example</chatserver>"
        + string.Concat(entries)
        + "</i>";

    private static string D(string p, string text) => $"<d p=\"{p}\">{text}</d>";

    private static Task<ParseRun> ParseAsync(string content, DanmukuParserLimits? limits = null, string? stagedName = null) =>
        ParseBytesAsync(Utf8NoBom.GetBytes(content), limits, stagedName);

    private static async Task<ParseRun> ParseBytesAsync(byte[] bytes, DanmukuParserLimits? limits = null, string? stagedName = null)
    {
        using var storage = new StorageTestContext();
        var stagedPath = Path.Combine(storage.Paths.StagingPath, stagedName ?? "staged.dat");
        var parser = new DanmukuFileParser(limits ?? DanmukuParserLimits.Default);
        var comments = new List<CommentRecord>();
        var errors = new List<ImportErrorRecord>();
        using var source = new MemoryStream(bytes, writable: false);

        var outcome = await parser.ParseAsync(
            source,
            stagedPath,
            (batch, _) =>
            {
                comments.AddRange(batch);
                return Task.CompletedTask;
            },
            (batch, _) =>
            {
                errors.AddRange(batch);
                return Task.CompletedTask;
            });

        return new ParseRun(outcome, comments, errors, stagedPath, bytes, File.ReadAllBytes(stagedPath));
    }

    private sealed record ParseRun(
        ParseOutcome Outcome,
        IReadOnlyList<CommentRecord> Comments,
        IReadOnlyList<ImportErrorRecord> Errors,
        string StagedPath,
        byte[] Input,
        byte[] Staged);
}
