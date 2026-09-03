using System.Text.Json;

using Shouldly;

namespace Sw5e.Database.Tests;

/// <summary>
/// The order a reader is walked through the Player's Handbook.
/// </summary>
/// <remarks>
/// <para>
/// The site does not read <c>chapterNumber</c>, and is not meant to. That field
/// records where a passage fell in a PDF, which is a fact about a book nobody
/// browsing a website is holding — and it actively misleads: the handbook
/// numbers "What's Different?" -1 so it sorts ahead of the introduction, which
/// is right for a reader who already plays 5e and wrong for one meeting the
/// game. It stays in the corpus because it is true about the archive, and the
/// site ignores it.
/// </para>
/// <para>
/// What the site reads is <c>order</c> and <c>readingGroup</c>: a position and
/// a heading, both authored, neither derived from anything. Fifteen links in a
/// row is a list; four groups of three or four is a path somebody can see the
/// shape of. Whoever owns the corpus can rearrange both by editing content,
/// without anybody touching the site.
/// </para>
/// <para>
/// Asserted here rather than in the site, because it is a fact about the
/// content. A page can be tested to sort by a field; only this can say the
/// field holds the right values.
/// </para>
/// </remarks>
public sealed class ReadingOrderTests
{
    private static readonly string RuleRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content", "rule");

    private static readonly string StarshipRuleRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content", "starship-rule");

    private sealed record Chapter(string Key, int? Order, string? Group);

    private static IReadOnlyList<Chapter> HandbookChapters() =>
        Chapters(RuleRoot, root =>
            root.TryGetProperty("sourceKey", out var source) &&
            source.GetString() == "phb" &&
            root.TryGetProperty("ruleType", out var kind) &&
            kind.GetString() == "chapter");

    /// <summary>
    /// Starships of the Galaxy, whose chapters are their own content type.
    /// </summary>
    /// <remarks>
    /// Every document of that type is a chapter of the one book, so there is
    /// nothing to filter on — which is why the predicate is a parameter rather
    /// than this method being a copy of the one above with two conditions
    /// removed.
    /// </remarks>
    private static IReadOnlyList<Chapter> StarshipChapters() =>
        Chapters(StarshipRuleRoot, _ => true);

    private static IReadOnlyList<Chapter> Chapters(
        string root,
        Func<JsonElement, bool> include)
    {
        var chapters = new List<Chapter>();

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var element = document.RootElement;

            if (!include(element)) continue;

            chapters.Add(new Chapter(
                element.GetProperty("key").GetString()!,
                element.TryGetProperty("order", out var order) ? order.GetInt32() : null,
                element.TryGetProperty("readingGroup", out var group)
                    ? group.GetString()
                    : null));
        }

        return chapters;
    }

    /// <summary>
    /// Every chapter of the handbook is placed.
    /// </summary>
    /// <remarks>
    /// An absent order is not a schema error: a variant rule has no place in a
    /// reading path and should not be forced to claim one. But the handbook is
    /// the path a new reader is walked down, and a chapter of it left unplaced
    /// simply would not appear — so an omission here is invisible rather than
    /// noisy, which is exactly the kind that needs a test.
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
    /// The introduction opens the path.
    /// </summary>
    /// <remarks>
    /// A reader who has never played is met with an explanation of what the
    /// game is. A reader who already plays 5e loses nothing, because the
    /// comparison is the very next thing.
    /// </remarks>
    [Fact]
    public void TheIntroductionOpensThePath()
    {
        var chapters = HandbookChapters().ToDictionary(chapter => chapter.Key);

        chapters["phb-introduction"].Order.ShouldBe(1);
        chapters["phb-introduction"].Order!.Value
            .ShouldBeLessThan(chapters["phb-whats-different"].Order!.Value);
    }

    /// <summary>
    /// Every placed chapter is read under a heading.
    /// </summary>
    /// <remarks>
    /// Fifteen links in a row is a list. Four groups of three or four is a path
    /// somebody can see the shape of before they start walking it, which is the
    /// difference between a table of contents and a wall.
    /// </remarks>
    [Fact]
    public void EveryPlacedChapterIsReadUnderAHeading()
    {
        var ungrouped = HandbookChapters()
            .Where(chapter => chapter.Order is not null && string.IsNullOrWhiteSpace(chapter.Group))
            .Select(chapter => chapter.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        ungrouped.ShouldBeEmpty("a placed chapter with no heading has nowhere to be rendered");
    }

    /// <summary>
    /// Each heading owns an unbroken run of the path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Groups are drawn in the order of their earliest member, so the grouping
    /// and the sequence are one decision rather than two that can disagree.
    /// That only holds while a group's members are contiguous: interleave two
    /// groups and the site must either reorder them — contradicting the
    /// authored positions — or draw the same heading twice.
    /// </para>
    /// <para>
    /// Nothing enforces this in the schema, because it is a property of the set
    /// rather than of any one document, which is exactly the kind of thing that
    /// is only ever caught here.
    /// </para>
    /// </remarks>
    [Fact]
    public void EachHeadingOwnsAnUnbrokenRunOfThePath()
    {
        var placed = HandbookChapters()
            .Where(chapter => chapter.Order is not null)
            .OrderBy(chapter => chapter.Order)
            .Select(chapter => chapter.Group)
            .ToArray();

        // Collapsing runs must leave every heading mentioned exactly once.
        var runs = new List<string?>();
        foreach (var group in placed)
        {
            if (runs.Count == 0 || runs[^1] != group) runs.Add(group);
        }

        runs.Distinct().Count().ShouldBe(
            runs.Count,
            "a heading appears in more than one place in the path, so its " +
            "members are interleaved with another group's");
    }

    /* ------------------------------------------ Starships of the Galaxy */

    /// <summary>
    /// The starship book has a path too, and it is held to the same rules.
    /// </summary>
    /// <remarks>
    /// Written as theories over both books rather than as a second copy of the
    /// three tests above. A duplicated file is how one of two books quietly
    /// stops being checked: somebody fixes the handbook's copy, the starship
    /// one keeps whatever it had, and nothing says so.
    /// </remarks>
    public static TheoryData<string> Books() => new() { "handbook", "starships" };

    private static IReadOnlyList<Chapter> Book(string name) =>
        name == "handbook" ? HandbookChapters() : StarshipChapters();

    [Theory]
    [MemberData(nameof(Books))]
    public void EveryChapterOfTheBookIsPlaced(string book)
    {
        Book(book)
            .Where(chapter => chapter.Order is null)
            .Select(chapter => chapter.Key)
            .Order(StringComparer.Ordinal)
            .ShouldBeEmpty($"an unplaced chapter of the {book} does not appear on its path at all");
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void EveryChapterOfTheBookHasAHeading(string book)
    {
        Book(book)
            .Where(chapter => chapter.Order is not null &&
                              string.IsNullOrWhiteSpace(chapter.Group))
            .Select(chapter => chapter.Key)
            .Order(StringComparer.Ordinal)
            .ShouldBeEmpty($"a placed chapter of the {book} with no heading has nowhere to render");
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void EachBookNumbersItsPathFromOne(string book)
    {
        var orders = Book(book)
            .Select(chapter => chapter.Order)
            .OfType<int>()
            .ToArray();

        orders.Order().ShouldBe(Enumerable.Range(1, orders.Length));
    }

    /// <summary>
    /// Each book's positions are its own.
    /// </summary>
    /// <remarks>
    /// Both paths start at one, and that is correct: they are two books and a
    /// reader walks one of them. Stated explicitly because the obvious mistake
    /// when adding the second was to continue the first book's numbering, which
    /// would look tidy, sort correctly, and mean that inserting a chapter into
    /// the handbook renumbered a book it has nothing to do with.
    /// </remarks>
    [Fact]
    public void TheTwoBooksNumberThemselvesIndependently()
    {
        HandbookChapters().Min(chapter => chapter.Order).ShouldBe(1);
        StarshipChapters().Min(chapter => chapter.Order).ShouldBe(1);
    }
}