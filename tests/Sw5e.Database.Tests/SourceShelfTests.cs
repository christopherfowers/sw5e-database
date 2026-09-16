using System.Text.Json;

using Shouldly;

namespace Sw5e.Database.Tests;

/// <summary>
/// How the books describe themselves.
/// </summary>
/// <remarks>
/// <para>
/// A book's name on the shelf, the line under it, the hue it is drawn in and
/// where it sits among the others were all held in the site, in a table that
/// had to be edited and deployed before a book could appear. They are facts
/// about a publication, so they live with the publication.
/// </para>
/// <para>
/// The schema keeps all four optional on purpose, and that is worth stating
/// because it looks like an oversight. A source that describes none of them
/// still works: the site falls back to a plain badge rather than drawing an
/// empty book. That degradation is what lets somebody add a supplement to the
/// corpus before anybody has written a sentence about it, which is the whole
/// point of the books being data, and a schema that demanded a blurb would
/// turn "not described yet" into "cannot be added".
/// </para>
/// <para>
/// What the schema cannot say is that <em>these five</em> are all described.
/// That is a fact about the committed corpus rather than about any one
/// document, which is exactly the kind of thing only a test here can hold.
/// </para>
/// </remarks>
public sealed class SourceShelfTests
{
    private static readonly string SourceRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content", "source");

    private sealed record Book(
        string Key,
        string? ShelfName,
        string? Blurb,
        string? Accent,
        int? Order,
        bool IsCoreRulebook);

    /// <summary>
    /// The palette a book may be drawn in.
    /// </summary>
    /// <remarks>
    /// Repeated from the site's <c>Accent</c> union rather than derived,
    /// because the two live in different repositories and neither can import
    /// the other. The schema enumerates the same set, so a value outside it is
    /// already refused before reaching here; this asserts the two lists have
    /// not drifted apart, which is the failure that would otherwise show up as
    /// a book rendering in no colour at all.
    /// </remarks>
    private static readonly string[] Palette =
    [
        "amber", "clay", "cyan", "green", "indigo",
        "red", "rose", "steel", "teal", "violet",
    ];

    private static IReadOnlyList<Book> Books()
    {
        var books = new List<Book>();

        foreach (var path in Directory.EnumerateFiles(SourceRoot, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            static string? Text(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) ? value.GetString() : null;

            books.Add(new Book(
                root.GetProperty("key").GetString()!,
                Text(root, "shelfName"),
                Text(root, "blurb"),
                Text(root, "accent"),
                root.TryGetProperty("order", out var order) ? order.GetInt32() : null,
                root.TryGetProperty("isCoreRulebook", out var core) && core.GetBoolean()));
        }

        return books;
    }

    /// <summary>
    /// Every book committed here is described.
    /// </summary>
    /// <remarks>
    /// A book with no blurb and no colour renders as a bare abbreviation, which
    /// is the right answer for a supplement nobody has written about yet and
    /// the wrong one for the five this reference is built from.
    /// </remarks>
    [Fact]
    public void EveryBookIsDescribed()
    {
        var undescribed = Books()
            .Where(book => string.IsNullOrWhiteSpace(book.Blurb) ||
                           string.IsNullOrWhiteSpace(book.Accent))
            .Select(book => book.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        undescribed.ShouldBeEmpty(
            "these books would be drawn as a bare abbreviation with no line under them");
    }

    /// <summary>
    /// No two books are drawn in the same colour.
    /// </summary>
    /// <remarks>
    /// The colour is how a reader tells at a glance which book a row came from,
    /// on a page mixing all five. Two books sharing a hue does not fail
    /// anywhere. It just quietly stops answering the question the colour
    /// exists to answer.
    /// </remarks>
    [Fact]
    public void NoTwoBooksShareAColour()
    {
        var shared = Books()
            .Where(book => book.Accent is not null)
            .GroupBy(book => book.Accent!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(book => book.Key).Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        shared.ShouldBeEmpty("a colour that names two books names neither");
    }

    [Fact]
    public void EveryColourIsOneTheSiteCanDraw()
    {
        var unknown = Books()
            .Where(book => book.Accent is not null && !Palette.Contains(book.Accent))
            .Select(book => $"{book.Key}: {book.Accent}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        unknown.ShouldBeEmpty("the site has no such accent and would draw the book in none");
    }

    /// <summary>
    /// The books are a run from one, with nothing repeated.
    /// </summary>
    /// <remarks>
    /// Same invariant as a book's chapters, for the same reason: two books
    /// sharing a position render in whatever order the tie break produces, so
    /// the shelf quietly reorders itself between builds rather than failing.
    /// </remarks>
    [Fact]
    public void TheBooksAreARunFromOneWithNothingRepeated()
    {
        var positions = Books().Select(book => book.Order).OfType<int>().ToArray();

        positions.Order().ShouldBe(Enumerable.Range(1, positions.Length));
    }

    /// <summary>
    /// The handbook comes first.
    /// </summary>
    /// <remarks>
    /// Stated rather than left to the numbers, because it is the one position
    /// on the shelf that is a decision rather than a sequence: a reader who
    /// does not know the game needs the core rulebook, not whichever supplement
    /// happens to sort first.
    /// </remarks>
    [Fact]
    public void TheHandbookComesFirst() =>
        Books().Single(book => book.Key == "phb").Order.ShouldBe(1);

    /// <summary>
    /// Exactly one book teaches the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which book a reader who knows nothing is sent to used to be a constant
    /// in the site, with a comment explaining that nothing in the data marked
    /// it. Something does now, and this is the invariant a boolean on each
    /// document cannot express on its own.
    /// </para>
    /// <para>
    /// Both failures are quiet. None set and the front page has no book to
    /// open with, so the button that teaches the game does not render at all.
    /// Two set and it picks whichever it meets first, which depends on the
    /// order the corpus happens to be read in.
    /// </para>
    /// </remarks>
    [Fact]
    public void ExactlyOneBookTeachesTheGame()
    {
        var teaching = Books()
            .Where(book => book.IsCoreRulebook)
            .Select(book => book.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        teaching.ShouldBe(
            ["phb"],
            "the front page opens with the book that teaches the game, and picks it from here");
    }

    /// <summary>
    /// A shelf name is only given where it differs from the title.
    /// </summary>
    /// <remarks>
    /// The handbook is titled "Star Wars 5e Player's Handbook" and is called
    /// "Player's Handbook" everywhere a reader meets it; the other four are
    /// called what they are titled. Repeating the title into
    /// <c>shelfName</c> would be a second place to edit a book's name, and the
    /// two would eventually disagree.
    /// </remarks>
    [Fact]
    public void AShelfNameIsOnlyGivenWhereItDiffersFromTheTitle()
    {
        var redundant = new List<string>();

        foreach (var path in Directory.EnumerateFiles(SourceRoot, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (!root.TryGetProperty("shelfName", out var shelf)) continue;

            if (shelf.GetString() == root.GetProperty("title").GetString())
            {
                redundant.Add(root.GetProperty("key").GetString()!);
            }
        }

        redundant.ShouldBeEmpty("this shelf name repeats the title, so the book has two names to keep in step");
    }
}
