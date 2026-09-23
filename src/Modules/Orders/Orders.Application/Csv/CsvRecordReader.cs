using System.Text;

namespace Orders.Application.Csv;

/// <summary>A physical CSV record and the 1-based line on which it starts.</summary>
public sealed record CsvRecord(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>
/// Strict RFC 4180 reader: comma separated, double-quote escaped, LF or CRLF terminated.
/// It is deliberately not a general spreadsheet reader. CSV-001 accepts exactly this shape and
/// rejects every other one instead of normalizing it, so an operator never confirms a batch whose
/// quoting the importer silently reinterpreted.
/// </summary>
public static class CsvRecordReader
{
    private const char Delimiter = ',';
    private const char Quote = '"';

    /// <summary>
    /// Splits <paramref name="text"/> into records. Returns false for every malformed document: a
    /// quoted field that is never terminated, a quote that appears anywhere inside an unquoted
    /// field, and any character other than a delimiter or a line break after a closing quote.
    /// Records that are entirely blank and unquoted are skipped, so trailing newlines are harmless.
    /// </summary>
    public static bool TryReadRecords(string text, out IReadOnlyList<CsvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(text);
        var completed = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = FieldState.Start;
        var quotedRecord = false;
        var line = 1;
        var recordLine = 1;

        void CompleteField()
        {
            fields.Add(field.ToString());
            field.Clear();
            state = FieldState.Start;
        }

        void CompleteRecord()
        {
            CompleteField();
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

            // Inside a quoted field every character is data until the next quote, which either
            // closes the field or, doubled, escapes a literal quote.
            if (state == FieldState.Quoted)
            {
                if (current == Quote)
                {
                    state = FieldState.Closed;
                    continue;
                }

                if (current == '\n')
                {
                    line++;
                }

                field.Append(current);
                continue;
            }

            if (state == FieldState.Closed && current == Quote)
            {
                field.Append(Quote);
                state = FieldState.Quoted;
                continue;
            }

            // A quote may open a field only as that field's very first character.
            if (state == FieldState.Start && current == Quote)
            {
                state = FieldState.Quoted;
                quotedRecord = true;
                continue;
            }

            switch (current)
            {
                case Delimiter:
                    CompleteField();
                    continue;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    CompleteRecord();
                    line++;
                    recordLine = line;
                    continue;
                case '\n':
                    CompleteRecord();
                    line++;
                    recordLine = line;
                    continue;
            }

            // Everything left is ordinary data, and only an unquoted field may carry it: a quote is
            // never legal inside an unquoted field, and a closed quoted field is already finished.
            if (current == Quote || state == FieldState.Closed)
            {
                records = [];
                return false;
            }

            state = FieldState.Unquoted;
            field.Append(current);
        }

        if (state == FieldState.Quoted)
        {
            records = [];
            return false;
        }

        CompleteRecord();
        records = completed;
        return true;
    }

    private enum FieldState
    {
        /// <summary>No character of the current field has been read; only here may a quote open it.</summary>
        Start,

        /// <summary>An unquoted field is being read; a quote inside it is malformed.</summary>
        Unquoted,

        /// <summary>A quoted field is open; it ends at the next unescaped quote.</summary>
        Quoted,

        /// <summary>A quoted field has been closed; only a delimiter, a line break or the end of file may follow.</summary>
        Closed,
    }
}
