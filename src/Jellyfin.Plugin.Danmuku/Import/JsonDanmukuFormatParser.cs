using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Streaming parser for the top-level JSON array structure. Each element is validated with
/// <see cref="Utf8JsonReader"/> over a bounded buffer; oversized elements are counted as
/// abnormal entries instead of being misreported as broken structure.
/// </summary>
internal static class JsonDanmukuFormatParser
{
    public static async Task ParseAsync(
        StagingTeeStream stream,
        ParseAccumulator accumulator,
        DanmukuParserLimits limits,
        CancellationToken cancellationToken)
    {
        var framer = new JsonStreamFramer(stream, limits);
        framer.ReadDocumentStart();

        while (framer.TryReadElement(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (frame.Oversized)
            {
                accumulator.BeginEntry();
                accumulator.AddReason(CommentAbnormalReason.EntryTooLarge);
                await accumulator.EndAbnormalAsync(null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var record = ParseElement(framer.CurrentElement, accumulator, limits, out var summary);
                if (record is not null)
                {
                    await accumulator.EndNormalAsync(record, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await accumulator.EndAbnormalAsync(summary, cancellationToken).ConfigureAwait(false);
                }
            }

            framer.AdvanceAfterElement(frame);
        }

        framer.ReadDocumentEnd();
    }

    private static CommentRecord? ParseElement(
        ReadOnlySpan<byte> element,
        ParseAccumulator accumulator,
        DanmukuParserLimits limits,
        out string? summary)
    {
        accumulator.BeginEntry();
        summary = null;

        var progressSeen = false;
        var progressValid = false;
        var progress = 0L;

        var modeSeen = false;
        var modeValid = false;
        var mode = 0L;

        var fontSizeSeen = false;
        var fontSizeValid = false;
        var fontSize = 0L;

        var colorSeen = false;
        var colorValid = false;
        var color = 0L;

        long? sourceTimeMs = null;
        string? senderHash = null;
        long? weight = null;
        long? attr = null;

        var contentSeen = false;
        var contentIsText = false;
        string? content = null;

        string? idStr = null;
        var idStrValid = false;
        string? numericIdRaw = null;

        try
        {
            var reader = new Utf8JsonReader(element, isFinalBlock: true, default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var name = reader.GetString() ?? string.Empty;
                if (!reader.Read())
                {
                    break;
                }

                switch (name)
                {
                    case "progress":
                        progressSeen = true;
                        progressValid = TryReadInteger(ref reader, out progress);
                        break;

                    case "mode":
                        modeSeen = true;
                        modeValid = TryReadInteger(ref reader, out mode);
                        break;

                    case "fontsize":
                        fontSizeSeen = true;
                        fontSizeValid = TryReadInteger(ref reader, out fontSize);
                        break;

                    case "color":
                        colorSeen = true;
                        colorValid = TryReadInteger(ref reader, out color);
                        break;

                    case "ctime":
                        if (TryReadInteger(ref reader, out var ctime) && ctime >= 0)
                        {
                            sourceTimeMs = ctime;
                        }
                        else if (reader.TokenType != JsonTokenType.Null)
                        {
                            accumulator.RecordUnparsedOptionalField("ctime");
                        }

                        break;

                    case "midHash":
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            var value = reader.GetString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                senderHash = value;
                            }
                        }
                        else if (reader.TokenType != JsonTokenType.Null)
                        {
                            accumulator.RecordUnparsedOptionalField("midHash");
                        }

                        break;

                    case "weight":
                        if (TryReadInteger(ref reader, out var weightValue))
                        {
                            weight = weightValue;
                        }
                        else if (reader.TokenType != JsonTokenType.Null)
                        {
                            accumulator.RecordUnparsedOptionalField("weight");
                        }

                        break;

                    case "attr":
                        if (TryReadInteger(ref reader, out var attrValue))
                        {
                            attr = attrValue;
                        }
                        else if (reader.TokenType != JsonTokenType.Null)
                        {
                            accumulator.RecordUnparsedOptionalField("attr");
                        }

                        break;

                    case "content":
                        contentSeen = true;
                        if (reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
                        {
                            contentIsText = true;
                            content = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        }

                        break;

                    case "idStr":
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            var value = reader.GetString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                idStr = value;
                                idStrValid = true;
                            }
                        }
                        else if (reader.TokenType != JsonTokenType.Null)
                        {
                            accumulator.RecordUnparsedOptionalField("idStr");
                        }

                        break;

                    case "id":
                        if (reader.TokenType == JsonTokenType.Number && IsIntegerToken(reader.ValueSpan))
                        {
                            // The raw digit text is kept to avoid numeric precision loss.
                            numericIdRaw = Encoding.UTF8.GetString(reader.ValueSpan);
                        }
                        else if (reader.TokenType != JsonTokenType.Null)
                        {
                            accumulator.RecordUnparsedOptionalField("id");
                        }

                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new DanmukuFormatRejectedException(
                ParseRejectReason.StructureInvalid,
                $"The JSON content is not valid: {exception.Message}",
                exception);
        }

        if (!progressSeen)
        {
            accumulator.AddReason(CommentAbnormalReason.MissingTime);
        }
        else if (!progressValid || progress < 0)
        {
            accumulator.AddReason(CommentAbnormalReason.InvalidTime);
        }

        var modeValue = DanmukuImportLimits.DefaultMode;
        if (modeSeen)
        {
            if (!modeValid || mode <= 0)
            {
                accumulator.AddReason(CommentAbnormalReason.InvalidMode);
            }
            else if (mode is not (1 or 4 or 5))
            {
                accumulator.AddReason(CommentAbnormalReason.UnsupportedMode);
                accumulator.RecordUnsupportedMode(mode);
            }
            else
            {
                modeValue = (int)mode;
            }
        }
        else
        {
            accumulator.RecordDefaultMode();
        }

        var fontSizeValue = DanmukuImportLimits.DefaultFontSize;
        if (fontSizeSeen)
        {
            if (!fontSizeValid || fontSize <= 0 || fontSize > int.MaxValue)
            {
                accumulator.AddReason(CommentAbnormalReason.InvalidFontSize);
            }
            else
            {
                fontSizeValue = (int)fontSize;
            }
        }
        else
        {
            accumulator.RecordDefaultFontSize();
        }

        var colorValue = DanmukuImportLimits.DefaultColor;
        if (colorSeen)
        {
            if (!colorValid || color < 0 || color > DanmukuImportLimits.MaxColor)
            {
                accumulator.AddReason(CommentAbnormalReason.InvalidColor);
            }
            else
            {
                colorValue = color;
            }
        }
        else
        {
            accumulator.RecordDefaultColor();
        }

        if (contentIsText)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                accumulator.AddReason(CommentAbnormalReason.BlankText);
            }
            else if (TextMetrics.ExceedsCodepoints(content, limits.MaxTextCodepoints))
            {
                accumulator.AddReason(CommentAbnormalReason.TextTooLong);
            }
        }
        else
        {
            accumulator.AddReason(contentSeen ? CommentAbnormalReason.InvalidText : CommentAbnormalReason.BlankText);
        }

        summary = TextMetrics.CreateSummary(content, limits.TextSummaryMaxCodepoints);

        if (accumulator.HasReasons)
        {
            return null;
        }

        string? sourceId = null;
        if (idStrValid)
        {
            sourceId = idStr;
        }
        else if (numericIdRaw is not null)
        {
            sourceId = numericIdRaw;
            accumulator.RecordIdFallback();
        }

        return new CommentRecord(
            accumulator.TotalEntries,
            sourceId,
            progress,
            content!,
            colorValue,
            modeValue,
            fontSizeValue,
            sourceTimeMs,
            senderHash,
            numericIdRaw,
            weight,
            attr,
            null);
    }

    private static bool TryReadInteger(ref Utf8JsonReader reader, out long value)
    {
        value = 0;
        return reader.TokenType == JsonTokenType.Number
            && IsIntegerToken(reader.ValueSpan)
            && reader.TryGetInt64(out value);
    }

    private static bool IsIntegerToken(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
        {
            return false;
        }

        var index = raw[0] == (byte)'-' ? 1 : 0;
        if (index == raw.Length)
        {
            return false;
        }

        for (; index < raw.Length; index++)
        {
            if (raw[index] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }
}
