using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Guards the credits content set: who is acknowledged, for what, and on what
/// footing the site shows their work.
/// </summary>
/// <remarks>
/// These assertions are deliberately specific. A credits page is the one part
/// of this repository where being approximately right is being wrong — the
/// failure mode is not a broken build but somebody's name spelled wrong, or
/// their acknowledgement for a particular piece of work quietly flattened into
/// membership of a list. None of that would turn a test red on its own, so the
/// tests below name real people and real contribution text taken from the
/// original site's credits, and they check the shape of the whole set rather
/// than that it merely parses. Every one of them fails against an empty
/// content directory.
/// </remarks>
public sealed class CreditContentTests
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    private static IReadOnlyList<JsonObject> Load(string contentType)
    {
        var directory = Path.Combine(ContentRoot, contentType);

        Directory.Exists(directory).ShouldBeTrue(
            $"No content directory at '{directory}'.");

        var documents = Directory
            .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(file => JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidOperationException($"{file} is not a JSON object."))
            .ToList();

        documents.ShouldNotBeEmpty($"No content files under '{directory}'.");
        return documents;
    }

    private static string? Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IReadOnlyList<JsonObject> InCategory(string category) =>
        Load("credit").Where(c => Text(c, "categoryKey") == category).ToList();

    private static JsonObject ByName(string category, string name) =>
        InCategory(category).SingleOrDefault(c => Text(c, "name") == name)
        ?? throw new InvalidOperationException(
            $"No credit named '{name}' in category '{category}'.");

    /// <summary>
    /// The population of every category, checked against a hand-count of the
    /// original credits document. Exact numbers rather than "greater than
    /// zero": the failure worth catching is a parser that dropped the tail of a
    /// comma-separated roster, and a loose bound would sail straight past it.
    /// </summary>
    [Theory]
    [InlineData("creator", 1)]
    [InlineData("personal-thanks", 4)]
    [InlineData("jedi-council", 20)]
    [InlineData("website-team", 4)]
    [InlineData("contributor", 52)]
    [InlineData("patron", 384)]
    [InlineData("art-asset", 57)]
    [InlineData("rights-holder", 1)]
    public void EveryCategoryHoldsTheNumberOfPeopleTheOriginalCreditsNamed(
        string category, int expected)
    {
        InCategory(category).Count.ShouldBe(expected);
    }

    /// <summary>
    /// The Jedi Council credits are the most valuable records in the archive:
    /// they are the only place anyone was credited for a specific piece of
    /// work rather than for taking part. Each is asserted verbatim, because
    /// the way this content degrades is somebody "tidying" a sentence until it
    /// no longer says what the person did.
    /// </summary>
    [Theory]
    [InlineData("Karbacca", "for the *epic* cover and SW5e logo")]
    [InlineData("Tomato-andrew", "for his immense help with the enhanced items")]
    [InlineData("Stormchaser6", "for his help with the Starships book")]
    [InlineData("Heresy", "for their excellent work with species")]
    [InlineData("Mishy", "for his excellent work on the Dawn of Defiance conversion")]
    [InlineData("Bob the Builder", "for his work on strongholds and feats")]
    public void AJediCouncilCreditKeepsTheContributionItWasGivenFor(
        string name, string contribution)
    {
        Text(ByName("jedi-council", name), "contribution").ShouldBe(contribution);
    }

    /// <summary>
    /// Every Jedi Council member has a specific contribution recorded. This is
    /// the assertion that stops the set being imported as bare names.
    /// </summary>
    [Fact]
    public void EveryJediCouncilCreditRecordsWhatThatPersonDid()
    {
        var withoutContribution = InCategory("jedi-council")
            .Where(credit => string.IsNullOrWhiteSpace(Text(credit, "contribution")))
            .Select(credit => Text(credit, "name"))
            .ToList();

        withoutContribution.ShouldBeEmpty(
            "the Jedi Council entries are the archive's only per-person records of " +
            "specific work and must not be reduced to a list of names");
    }

    /// <summary>
    /// The categories carry different meanings, so they must stay separate.
    /// A patron paid for the hosting; an artist's picture is on a page; a
    /// council member wrote a book of rules. Collapsing them into one roll
    /// takes the specific acknowledgement away from all three.
    /// </summary>
    [Fact]
    public void CategoriesAreNotCollapsedIntoOneAnother()
    {
        var categories = Load("credit-category");

        categories.Count.ShouldBe(8);
        categories
            .Select(category => Text(category, "key"))
            .ShouldBe(
                [
                    "art-asset", "contributor", "creator", "jedi-council",
                    "patron", "personal-thanks", "rights-holder", "website-team",
                ],
                ignoreOrder: true);

        // The same handle appears in more than one category, and each of those
        // is a different debt. DarkMesa wrote archetypes and also reviewed
        // other people's content; merging the two would silently discard one.
        InCategory("jedi-council").Any(c => Text(c, "name") == "DarkMesa").ShouldBeTrue();
        InCategory("contributor").Any(c => Text(c, "name") == "DarkMesa").ShouldBeTrue();

        // Nobody is filed under a category that does not exist, and no category
        // is left with nobody in it.
        var populated = Load("credit")
            .Select(credit => Text(credit, "categoryKey"))
            .ToHashSet(StringComparer.Ordinal);

        populated.ShouldBe(
            categories.Select(category => Text(category, "key")).ToHashSet(StringComparer.Ordinal),
            ignoreOrder: true);
    }

    /// <summary>
    /// Category ordering is authored, because the sequence means something:
    /// the people who made the thing come before the people who funded it, and
    /// the rights holder comes last.
    /// </summary>
    [Fact]
    public void CategoriesCarryAContiguousAuthoredOrder()
    {
        var orders = Load("credit-category")
            .Select(category => (int)category["order"]!)
            .Order()
            .ToList();

        orders.ShouldBe(Enumerable.Range(1, orders.Count).ToList());
    }

    /// <summary>
    /// Within a category the order must be a permutation of 1..n. A duplicate
    /// or a gap is how two people end up sharing a slot and one of them stops
    /// being rendered.
    /// </summary>
    [Fact]
    public void EveryCategoryOrdersItsPeopleWithoutGapsOrTies()
    {
        var failures = new List<string>();

        foreach (var group in Load("credit").GroupBy(credit => Text(credit, "categoryKey")))
        {
            var orders = group.Select(credit => (int)credit["order"]!).Order().ToList();
            var expected = Enumerable.Range(1, orders.Count).ToList();

            if (!orders.SequenceEqual(expected))
            {
                failures.Add($"{group.Key}: order values were [{string.Join(", ", orders)}]");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Two patron names reached the archive with an accented letter destroyed
    /// by the 2022 scrape, and they are carried here repaired.
    /// </summary>
    /// <remarks>
    /// The corpus normalizer refuses to guess an accented letter inside a
    /// proper noun, which is right for an invented species name where there is
    /// no ground truth to check against. These two are the other case: real
    /// personal names whose surviving letters admit one spelling each. The
    /// assertion is on the repaired form rather than on the mere absence of
    /// U+FFFD, so that a future import which drops the accent entirely — and
    /// so passes the replacement-character check — still fails here.
    /// </remarks>
    [Theory]
    [InlineData("César Díaz")]
    [InlineData("João Lira")]
    public void ANameTheArchiveDamagedIsCarriedRepaired(string name)
    {
        var patrons = InCategory("patron").Select(credit => Text(credit, "name")).ToList();

        patrons.ShouldContain(name);
    }

    /// <summary>
    /// Names are reproduced as their owners wrote them. Leading or trailing
    /// whitespace is the tell for a roster split on commas without trimming,
    /// which is exactly how "Aziz" becomes " Aziz" and sorts to the top.
    /// </summary>
    [Fact]
    public void NoNameCarriesStrayWhitespaceOrIsEmpty()
    {
        var failures = Load("credit")
            .Select(credit => Text(credit, "name")!)
            .Where(name => name.Length == 0 || name.Trim() != name)
            .ToList();

        failures.ShouldBeEmpty($"badly trimmed names: [{string.Join("|", failures)}]");
    }

    /// <summary>
    /// The one image in the whole set whose artist the archive actually
    /// records. The original credits name Karbacca "for the epic cover and
    /// SW5e logo", and the site's logo is built from that file.
    /// </summary>
    [Fact]
    public void TheSiteLogoIsCitedToItsArtist()
    {
        var logo = Load("asset-credit")
            .Single(credit => Text(credit, "key") == "brand-logo");

        Text(logo, "status").ShouldBe("cited");
        Text(logo, "artist").ShouldBe("Karbacca");
        Text(logo, "workTitle").ShouldBe("SW5e logo");
        Text(logo, "basis").ShouldBe("fan-content-policy");
        Text(logo, "provenance").ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Every other picture on the site is recorded as inherited with its artist
    /// unknown, and none of them pretends otherwise.
    /// </summary>
    /// <remarks>
    /// This is the assertion that makes the upload contract real. The
    /// inherited state exists only for work that was already on the site when
    /// this record was written, so the count is frozen: a 150th inherited
    /// record means somebody added a picture without citing it, and this test
    /// is what stops that reaching main. Raising the number is not the fix.
    /// </remarks>
    [Fact]
    public void EveryInheritedImageAdmitsItsArtistIsUnknownRatherThanGuessing()
    {
        var credits = Load("asset-credit");
        var inherited = credits
            .Where(credit => Text(credit, "status") == "inherited-unattributed")
            .ToList();

        credits.Count.ShouldBe(150);
        inherited.Count.ShouldBe(149);

        foreach (var credit in inherited)
        {
            var key = Text(credit, "key");

            credit.ContainsKey("artist").ShouldBeFalse(
                $"{key} names an artist while claiming not to know one");
            credit.ContainsKey("workTitle").ShouldBeFalse(
                $"{key} names a work while claiming not to know one");
            Text(credit, "basis").ShouldBe("unrecorded", key);
            Text(credit, "provenance").ShouldNotBeNullOrWhiteSpace(key);
        }
    }

    /// <summary>
    /// Every asset citation points at a real group, and no two point at the
    /// same picture. A duplicate would mean one image with two different
    /// stories about who made it.
    /// </summary>
    [Fact]
    public void EveryPictureHasExactlyOneCitation()
    {
        var credits = Load("asset-credit");

        var targets = credits
            .Select(credit => $"{Text(credit, "assetGroup")}/{Text(credit, "assetKey")}")
            .ToList();

        targets.Distinct(StringComparer.Ordinal).Count().ShouldBe(targets.Count);

        credits
            .Select(credit => Text(credit, "assetGroup"))
            .Distinct()
            .ShouldBe(["brand", "classes", "sources", "species"], ignoreOrder: true);

        // The key is derived from the target, so a mismatch means one of the
        // two was edited without the other and the citation now describes a
        // different picture than its file name claims.
        foreach (var credit in credits)
        {
            Text(credit, "key").ShouldBe(
                $"{Text(credit, "assetGroup")}-{Text(credit, "assetKey")}");
        }
    }
}
