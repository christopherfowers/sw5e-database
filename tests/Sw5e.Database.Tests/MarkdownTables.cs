using System.Text.RegularExpressions;

namespace Sw5e.Database.Tests;

/// <summary>
/// Tables the source books printed in two columns and the archive recorded as
/// two tables.
/// </summary>
/// <remarks>
/// <para>
/// A wide table set as two side-by-side columns comes through the archive as a
/// header, the first half of the rows, then the same header again and the rest.
/// A reader looking up the point cost of a 6th-level power finds a table that
/// stops at 4 and a second one underneath starting at 5.
/// </para>
/// <para>
/// <b>Adjacency and a shared header are not enough to act on.</b>
/// <c>ec-backgrounds</c> carries forty <c>|d8|Feat|</c> tables — one per
/// background — and merging those would destroy the document. The signal is a
/// <em>continuation</em>: the second table's first column picks up where the
/// first's leaves off, with no value in both. Two tables each running 1–8 are
/// two tables; one running 1–4 followed by one running 5–8 is one table cut in
/// half.
/// </para>
/// <para>
/// This lives in one place because two callers need the same answer and must
/// not be able to disagree: the import rejoins as text enters the corpus, and
/// <see cref="SplitTableTests"/> asserts none survive. Two implementations of
/// "is this split" would eventually let a split through while reporting none.
/// </para>
/// </remarks>
public static class MarkdownTables
{
    private static readonly Regex Row = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);
    private static readonly Regex Alignment = new(@"^\s*\|[-: |]+\|\s*$", RegexOptions.Compiled);
    private static readonly Regex Range = new(@"^(\d{1,3})\s*[-–]\s*(\d{1,3})$", RegexOptions.Compiled);
    private static readonly Regex Single = new(@"^(\d{1,3})$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private sealed record Table(string Header, string Alignment, List<string> Rows, int End);

    private static string Squash(string value) => Whitespace.Replace(value, string.Empty);

    /// <summary>
    /// Every value a first-column cell stands for, or null when it is not numeric.
    /// </summary>
    /// <remarks>
    /// A d100 table's cells are ranges — <c>01-02</c>, <c>99-100</c> — so one
    /// cell covers many values and overlap has to be tested across the whole
    /// span. Reading only the first number of each range is how an early survey
    /// of this fault reported a table ending at 75 when it ran to 100.
    /// </remarks>
    private static List<int>? ValuesOf(string cell)
    {
        var range = Range.Match(cell);
        if (range.Success)
        {
            var from = int.Parse(range.Groups[1].Value);
            var to = int.Parse(range.Groups[2].Value);
            return to < from ? null : [.. Enumerable.Range(from, to - from + 1)];
        }

        var single = Single.Match(cell);
        return single.Success ? [int.Parse(single.Groups[1].Value)] : null;
    }

    private static List<int>? SpanOf(IEnumerable<string> rows)
    {
        var values = new List<int>();

        foreach (var row in rows)
        {
            var first = row.Trim().Trim('|').Split('|').FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(first))
            {
                return null;
            }

            var parsed = ValuesOf(first);
            if (parsed is null)
            {
                return null;
            }

            values.AddRange(parsed);
        }

        return values.Count > 0 ? values : null;
    }

    private static bool Continues(List<string> first, List<string> second)
    {
        var a = SpanOf(first);
        var b = SpanOf(second);
        if (a is null || b is null)
        {
            return false;
        }

        var seen = a.ToHashSet();
        return !b.Any(seen.Contains) && b.Min() > a.Max();
    }

    private static Table? ReadTable(string[] lines, int index)
    {
        if (index + 1 >= lines.Length) return null;
        if (!Row.IsMatch(lines[index]) || Alignment.IsMatch(lines[index])) return null;
        if (!Alignment.IsMatch(lines[index + 1])) return null;

        var rows = new List<string>();
        var cursor = index + 2;
        while (cursor < lines.Length && Row.IsMatch(lines[cursor]))
        {
            rows.Add(lines[cursor]);
            cursor++;
        }

        return rows.Count == 0 ? null : new Table(lines[index], lines[index + 1], rows, cursor);
    }

    /// <summary>The table that follows this one, if it continues it.</summary>
    private static Table? Continuation(string[] lines, Table table)
    {
        var cursor = table.End;
        while (cursor < lines.Length && lines[cursor].Trim().Length == 0)
        {
            cursor++;
        }

        var next = ReadTable(lines, cursor);
        if (next is null) return null;
        if (Squash(next.Header) != Squash(table.Header)) return null;
        if (Squash(next.Alignment) != Squash(table.Alignment)) return null;

        return Continues(table.Rows, next.Rows) ? next : null;
    }

    /// <summary>The header of every table in this text that is cut in half.</summary>
    public static IEnumerable<string> SplitIn(string text)
    {
        if (!text.Contains('|')) yield break;

        var lines = text.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var table = ReadTable(lines, index);
            if (table is null)
            {
                index++;
                continue;
            }

            if (Continuation(lines, table) is not null)
            {
                yield return table.Header.Trim();
            }

            index = table.End;
        }
    }

    /// <summary>
    /// The same text with every split table rejoined.
    /// </summary>
    /// <remarks>
    /// Returns the input unchanged when there is nothing to do, which is the
    /// overwhelmingly common case — this runs over every string the archive
    /// carries, and almost none of them hold a table at all.
    /// </remarks>
    public static string Rejoin(string text)
    {
        if (!text.Contains('|')) return text;

        var lines = text.Split('\n');
        var output = new List<string>(lines.Length);
        var changed = false;
        var index = 0;

        while (index < lines.Length)
        {
            var table = ReadTable(lines, index);
            if (table is null)
            {
                output.Add(lines[index]);
                index++;
                continue;
            }

            var rows = new List<string>(table.Rows);
            var end = table.End;

            // Looped rather than done once, so a table printed in three columns
            // folds left to right in a single pass.
            for (var next = Continuation(lines, table with { Rows = rows, End = end });
                 next is not null;
                 next = Continuation(lines, table with { Rows = rows, End = end }))
            {
                rows.AddRange(next.Rows);
                end = next.End;
                changed = true;
            }

            output.Add(table.Header);
            output.Add(table.Alignment);
            output.AddRange(rows);
            index = end;
        }

        return changed ? string.Join('\n', output) : text;
    }
}
