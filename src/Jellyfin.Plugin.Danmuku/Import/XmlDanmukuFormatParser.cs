using System.Xml;
using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Streaming parser for the Bilibili XML structure: an <c>i</c> root whose <c>d</c> children
/// carry the fixed-position <c>p</c> attribute and the comment text. Uses a pull reader and
/// never builds a document tree; DTDs and external entities are disabled.
/// </summary>
internal static class XmlDanmukuFormatParser
{
    public static async Task ParseAsync(
        StagingTeeStream stream,
        ParseAccumulator accumulator,
        DanmukuParserLimits limits,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false
        };

        var textChunk = new char[4096];
        var sawDanmukuElement = false;

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            if (reader.MoveToContent() != XmlNodeType.Element)
            {
                throw Structure("The XML document has no root element.");
            }

            if (!string.Equals(reader.LocalName, "i", StringComparison.Ordinal))
            {
                throw Structure($"The XML root element must be 'i', but was '{reader.LocalName}'.");
            }

            var rootDepth = reader.Depth;
            if (reader.IsEmptyElement)
            {
                reader.Read();
            }
            else
            {
                reader.Read();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (reader.NodeType == XmlNodeType.EndElement
                        && reader.Depth == rootDepth
                        && string.Equals(reader.LocalName, "i", StringComparison.Ordinal))
                    {
                        reader.Read();
                        break;
                    }

                    if (reader.NodeType == XmlNodeType.Element
                        && reader.Depth == rootDepth + 1
                        && string.Equals(reader.LocalName, "d", StringComparison.Ordinal))
                    {
                        sawDanmukuElement |= await ParseEntryAsync(reader, accumulator, limits, textChunk, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        // Unknown root metadata such as chatserver/chatid is tolerated.
                        reader.Skip();
                        continue;
                    }

                    if (!reader.Read())
                    {
                        break;
                    }
                }
            }

            // Reads to the end of the document so trailing garbage is rejected by the reader.
            while (reader.Read())
            {
            }
        }
        catch (XmlException exception)
        {
            throw Structure($"The XML content is not valid: {exception.Message}", exception);
        }

        if (!sawDanmukuElement)
        {
            throw Structure("The XML document has no 'd' element with a 'p' attribute.");
        }
    }

    private static async Task<bool> ParseEntryAsync(
        XmlReader reader,
        ParseAccumulator accumulator,
        DanmukuParserLimits limits,
        char[] textChunk,
        CancellationToken cancellationToken)
    {
        var p = reader.GetAttribute("p");
        accumulator.BeginEntry();

        var text = ReadElementText(reader, limits, textChunk);
        var fields = p is null ? [] : p.Split(',');

        var timeMs = 0L;
        var timeValid = false;
        if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
        {
            accumulator.AddReason(CommentAbnormalReason.MissingTime);
        }
        else if (IntegerParsing.TryParseSecondsToMilliseconds(fields[0], out timeMs))
        {
            timeValid = true;
        }
        else
        {
            accumulator.AddReason(CommentAbnormalReason.InvalidTime);
        }

        var mode = DanmukuImportLimits.DefaultMode;
        if (fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1]))
        {
            if (IntegerParsing.TryParse(fields[1], out var modeValue) && modeValue > 0)
            {
                if (modeValue is 1 or 4 or 5)
                {
                    mode = (int)modeValue;
                }
                else
                {
                    accumulator.AddReason(CommentAbnormalReason.UnsupportedMode);
                    accumulator.RecordUnsupportedMode(modeValue);
                }
            }
            else
            {
                accumulator.AddReason(CommentAbnormalReason.InvalidMode);
            }
        }
        else
        {
            accumulator.RecordDefaultMode();
        }

        var fontSize = DanmukuImportLimits.DefaultFontSize;
        if (fields.Length > 2 && !string.IsNullOrWhiteSpace(fields[2]))
        {
            if (IntegerParsing.TryParse(fields[2], out var fontSizeValue)
                && fontSizeValue > 0
                && fontSizeValue <= int.MaxValue)
            {
                fontSize = (int)fontSizeValue;
            }
            else
            {
                accumulator.AddReason(CommentAbnormalReason.InvalidFontSize);
            }
        }
        else
        {
            accumulator.RecordDefaultFontSize();
        }

        var color = DanmukuImportLimits.DefaultColor;
        if (fields.Length > 3 && !string.IsNullOrWhiteSpace(fields[3]))
        {
            if (IntegerParsing.TryParse(fields[3], out var colorValue)
                && colorValue >= 0
                && colorValue <= DanmukuImportLimits.MaxColor)
            {
                color = colorValue;
            }
            else
            {
                accumulator.AddReason(CommentAbnormalReason.InvalidColor);
            }
        }
        else
        {
            accumulator.RecordDefaultColor();
        }

        long? sourceTimeMs = null;
        if (fields.Length > 4 && !string.IsNullOrWhiteSpace(fields[4]))
        {
            // XML p[4] carries seconds (unlike the JSON ctime field, which is already milliseconds).
            if (IntegerParsing.TryParseSecondsToMilliseconds(fields[4], out var ctimeMs))
            {
                sourceTimeMs = ctimeMs;
            }
            else
            {
                accumulator.RecordUnparsedOptionalField("ctime");
            }
        }

        long? pool = null;
        if (fields.Length > 5 && !string.IsNullOrWhiteSpace(fields[5]))
        {
            if (IntegerParsing.TryParse(fields[5], out var poolValue))
            {
                pool = poolValue;
            }
            else
            {
                accumulator.RecordUnparsedOptionalField("pool");
            }
        }

        string? senderHash = null;
        if (fields.Length > 6 && !string.IsNullOrWhiteSpace(fields[6]))
        {
            senderHash = fields[6];
        }

        string? sourceId = null;
        if (fields.Length > 7 && !string.IsNullOrWhiteSpace(fields[7]))
        {
            sourceId = fields[7];
        }

        if (text.IsAllWhitespace)
        {
            accumulator.AddReason(CommentAbnormalReason.BlankText);
        }

        if (text.OverLimit)
        {
            accumulator.AddReason(CommentAbnormalReason.TextTooLong);
        }

        if (!accumulator.HasReasons && timeValid)
        {
            var record = new CommentRecord(
                accumulator.TotalEntries,
                sourceId,
                timeMs,
                text.Text,
                color,
                mode,
                fontSize,
                sourceTimeMs,
                senderHash,
                null,
                null,
                null,
                pool);
            await accumulator.EndNormalAsync(record, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await accumulator.EndAbnormalAsync(text.Summary, cancellationToken).ConfigureAwait(false);
        }

        return p is not null;
    }

    private static TextAccumulator ReadElementText(
        XmlReader reader,
        DanmukuParserLimits limits,
        char[] textChunk)
    {
        var text = new TextAccumulator(limits.MaxTextCodepoints, limits.TextSummaryMaxCodepoints);
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return text;
        }

        if (!reader.Read())
        {
            return text;
        }

        while (true)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    while (true)
                    {
                        var read = reader.ReadValueChunk(textChunk, 0, textChunk.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        text.Append(textChunk.AsSpan(0, read));
                    }

                    if (!reader.Read())
                    {
                        return text;
                    }

                    break;

                case XmlNodeType.EndElement:
                    reader.Read();
                    return text;

                case XmlNodeType.Element:
                    // Nested markup inside the comment is not part of the plain text.
                    reader.Skip();
                    break;

                default:
                    if (!reader.Read())
                    {
                        return text;
                    }

                    break;
            }
        }
    }

    private static DanmukuFormatRejectedException Structure(string message, Exception? innerException = null) =>
        new(ParseRejectReason.StructureInvalid, message, innerException);
}
