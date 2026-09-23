using System.Text;

namespace Orders.Application.Csv;

/// <summary>A physical CSV record and the 1-based line on which it starts.</summary>
public sealed record CsvRecord(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>
/// Minimal RFC 4180 reader: comma separated, double-quote escaped, LF or CRLF terminated.
/// It is deliberately not a general spreadsheet reader; CSV-001 accepts exactly this shape.
/// </summary>
public static class CsvRecordReader
{
    private const char Delimiter = ',';
    private const char Quote = '"';

    /// <summary>
    /// Splits <paramref name="text"/> into records. Returns false when a quoted field is never
    /// terminated, which makes the remainder of the file unreadable and therefore unreportable.
    /// Records that are entirely blank and unquoted are skipped, so trailing newlines are harmless.
    /// </summary>
    public static bool TryReadRecords(string text, out IReadOnlyList<CsvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(text);
        var completed = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quotedRecord = false;
        var line = 1;
        var recordLine = 1;

        void CompleteRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            if (fields.Count > 1 || quotedRecord || fields[0].Length > 0)
            {
                completed.Add(new CsvRecord(recordLine, fields.ToArray()));
            }

            fields.Clear();
            quotedRecord = false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inQuotes)
            {
                if (current != Quote)
                {
                    if (current == '\n')
                    {
                        line++;
                    }

                    field.Append(current);
                    continue;
                }

                if (index + 1 < text.Length && text[index + 1] == Quote)
                {
                    field.Append(Quote);
                    index++;
                    continue;
                }

                inQuotes = false;
                continue;
            }

            switch (current)
            {
                case Quote:
                    inQuotes = true;
                    quotedRecord = true;
                    break;
                case Delimiter:
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    CompleteRecord();
                    line++;
                    recordLine = line;
                    break;
                case '\n':
                    CompleteRecord();
                    line++;
                    recordLine = line;
                    break;
                default:
                    field.Append(current);
                    break;
            }
        }

        if (inQuotes)
        {
            records = [];
            return false;
        }

        CompleteRecord();
        records = completed;
        return true;
    }
}
