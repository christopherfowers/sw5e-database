using System.Text.Json;
using System.Text.RegularExpressions;

using Shouldly;

namespace Sw5e.Database.Tests;

/// <summary>
/// Tables that were printed in two columns and read as two tables.
/// </summary>
/// <remarks>
/// <para>
/// The source books set wide tables as two side-by-side columns, and the
/// conversion took each column for a table of its own. Ninety-four of them came
/// through that way: a header, the first half of the rows, then the same header
/// again and the rest — so a reader looking up the point cost of a 6th-level
/// power found a table that stopped at 4 and a second one underneath starting
/// at 5.
/// </para>
/// <para>
/// <c>tools/rejoin_tables.mjs</c> rejoined them. This is what stops them coming
/// back, which matters because the next import from the same source will
/// reintroduce exactly the same shape and nothing about it looks wrong in a
/// diff.
/// </para>
/// <para>
/// <b>Adjacency and a shared header are not enough.</b> This file holds fifty
/// <c>|d8|Feat|</c> tables — one per background — and they are legitimately
/// separate. The signal is a <em>continuation</em>: the second table's first
/// column picks up where the first's leaves off, with no value in both. Two
/// tables that each run 1–8 are two tables; one running 1–4 followed by one
/// running 5–8 is one table cut in half.
/// </para>
/// </remarks>
public sealed class SplitTableTests
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    private static void Collect(JsonElement element, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into.Add(element.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, into);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) Collect(property.Value, into);
                break;
        }
    }

    [Fact]
    public void NoTableIsPrintedAsTwo()
    {
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ContentRoot, "*.json", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));

            var texts = new List<string>();
            Collect(document.RootElement, texts);

            foreach (var header in texts.Where(text => text.Contains('|')).SelectMany(MarkdownTables.SplitIn))
            {
                found.Add($"{Path.GetRelativePath(ContentRoot, file)}: {header}");
            }
        }

        found.ShouldBeEmpty(
            "these tables carry on into a second table with the same header, " +
            "which renders as one table cut in half with its heading repeated. " +
            "Run tools/rejoin_tables.mjs");
    }

    /// <summary>
    /// And the tables that only look alike are still separate.
    /// </summary>
    /// <remarks>
    /// The guard on the guard. A rejoin that was too eager would merge the
    /// forty per-background feat tables into one, and the test above would
    /// still pass — it only ever looks for splits. This is the assertion that
    /// notices the opposite mistake, which is the destructive one.
    /// </remarks>
    [Fact]
    public void BackgroundsKeepOneFeatTableEach()
    {
        var text = File.ReadAllText(Path.Combine(ContentRoot, "rule", "ec-backgrounds.json"));

        Regex.Matches(text, @"\|d8\|Feat\|").Count.ShouldBeGreaterThan(30,
            "each background offers its own feats; one merged table would be a " +
            "single list nobody could attribute");
    }
}
