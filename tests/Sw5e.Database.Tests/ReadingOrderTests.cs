using System.Text.Json;

using Shouldly;

namespace Sw5e.Database.Tests;

/// <summary>
/// The order a reader is walked through the Player's Handbook.
/// </summary>
/// <remarks>
/// <para>
/// <c>chapterNumber</c> records what the book printed and is a fact about the
/// archive. <c>order</c> records what the site teaches and is an editorial
/// decision, which is why it is a separate field and why it is expected to
/// change: the intent is that whoever owns the content sets it, without anybody
/// touching the site.
/// </para>
/// <para>
/// The two differ today in exactly one place, and that place is the reason the
/// field exists. The handbook numbers "What's Different?" -1 so it sorts ahead
/// of the introduction, which is the right order for somebody who already plays
/// 5e and the wrong one for somebody meeting the game for the first time. The
/// site this one replaces has always opened with the introduction, so that is
/// what the authored order says.
/// </para>
/// <para>
/// Asserted here rather than left to the site, because it is a fact about the
/// content and the site is only the thing that renders it. A page can be tested
/// to sort by a field; only this can say the field holds the right values.
/// </para>
/// </remarks>
public sealed class ReadingOrderTests
{
    private static readonly string RuleRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content", "rule");

    private sealed record Chapter(string Key, int? Order, int? ChapterNumber);

    private static IReadOnlyList<Chapter> HandbookChapters()
    {
        var chapters = new List<Chapter>();

        foreach (var path in Directory.EnumerateFiles(RuleRoot, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.TryGetProperty("sourceKey", out var source) &&
                source.GetString() == "phb" &&
                root.TryGetProperty("ruleType", out var kind) &&
                kind.GetString() == "chapter")
            {
                chapters.Add(new Chapter(
                    root.GetProperty("key").GetString()!,
                    root.TryGetProperty("order", out var order) ? order.GetInt32() : null,
                    root.TryGetProperty("chapterNumber", out var number)
                        ? number.GetInt32()
                        : null));
            }
        }

        return chapters;
    }

    /// <summary>
    /// Every chapter of the handbook is placed.
    /// </summary>
    /// <remarks>
    /// An absent order is not an error in the schema — a passage nobody has
    /// placed falls back to the number its book printed, which is a better
    /// answer than dropping it to the end of the list. But the handbook is the
    /// path a new reader is walked down, and a chapter of it left unplaced is
    /// an omission rather than a decision.
    /// </remarks>
    [Fact]
    public void EveryHandbookChapterIsPlaced()
    {
        var unplaced = HandbookChapters()
            .Where(chapter => chapter.Order is null)
            .Select(chapter => chapter.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        unplaced.ShouldBeEmpty(
            "these chapters would fall back to the order the book printed, " +
            "which is the thing the authored order exists to override");
    }

    /// <summary>
    /// The positions are a run from one, with nothing repeated.
    /// </summary>
    /// <remarks>
    /// A duplicate is the failure worth catching. Two chapters sharing a
    /// position do not fail anywhere — they render, in whatever order the tie
    /// break happens to produce — so the first anybody knows about it is a
    /// reader finding the combat chapter before the one that explains dice.
    /// </remarks>
    [Fact]
    public void ThePositionsAreARunFromOneWithNothingRepeated()
    {
        var orders = HandbookChapters()
            .Select(chapter => chapter.Order)
            .OfType<int>()
            .ToArray();

        orders.Order().ShouldBe(Enumerable.Range(1, orders.Length));
    }

    /// <summary>
    /// The introduction comes first, and before "What's Different?".
    /// </summary>
    /// <remarks>
    /// The one editorial decision in the whole ordering, pinned so that it
    /// cannot be undone by somebody regenerating positions from
    /// <c>chapterNumber</c> and producing a file that looks tidy. A reader who
    /// has never played is met with an explanation of what the game is; a
    /// reader who already plays 5e loses nothing, because the comparison is the
    /// very next thing.
    /// </remarks>
    [Fact]
    public void TheIntroductionOpensTheBookRatherThanTheComparisonWithFifthEdition()
    {
        var chapters = HandbookChapters().ToDictionary(chapter => chapter.Key);

        var introduction = chapters["phb-introduction"];
        var whatsDifferent = chapters["phb-whats-different"];

        introduction.Order.ShouldBe(1);
        introduction.Order!.Value.ShouldBeLessThan(whatsDifferent.Order!.Value);

        // And the printed numbers still disagree, which is the whole point: if
        // these ever matched, the authored order would be doing nothing and
        // this file would be pinning a coincidence.
        introduction.ChapterNumber!.Value
            .ShouldBeGreaterThan(whatsDifferent.ChapterNumber!.Value);
    }
}
