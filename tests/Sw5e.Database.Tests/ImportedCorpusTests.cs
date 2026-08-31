using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Guards the corpus imported from the legacy archive: the enhanced items, the
/// two property glossaries, the rules prose and the reference tables.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeedContentTests"/> already proves every document validates and
/// resolves its cross-references. What it cannot prove is that the documents
/// are <em>there</em>. A build that imported nothing, or imported a tenth of
/// the corpus because a loop stopped early, satisfies every assertion in that
/// class trivially — an empty set validates.
/// </para>
/// <para>
/// So the counts below are exact and derived from the archive rather than from
/// the content directory: 1,918 enhanced items, 46 weapon properties, 30 armour
/// properties, 76 rules records and 33 reference tables, less the four records
/// the archive holds as empty shells. Asserting the number against the
/// directory itself would be circular, which is why the archive count and the
/// deliberate exclusions are both spelled out here.
/// </para>
/// </remarks>
public sealed class ImportedCorpusTests
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    /// <summary>
    /// Content type, how many records the archive holds, and how many of those
    /// are deliberately not imported. The exclusions are the records whose body
    /// the scrape lost entirely; each is named in
    /// <c>ArchiveConformanceTests.KnownCorruptItems</c> with its reason.
    /// </summary>
    public static TheoryData<string, int, int> ExpectedCounts =>
        new()
        {
            { "enhanced-item", 1918, 0 },
            { "weapon-property", 46, 0 },
            { "armor-property", 30, 0 },
            { "rule", 76, 1 },
            { "reference-table", 33, 3 },
        };

    private static IReadOnlyList<(string Key, JsonObject Document)> Load(string contentType)
    {
        var directory = Path.Combine(ContentRoot, contentType);

        Directory.Exists(directory).ShouldBeTrue(
            $"No content directory at '{directory}'. Run " +
            "'dotnet run --project src/Sw5e.Database.Tools -- import-legacy <archive>'.");

        return Directory
            .EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(file => (
                Key: Path.GetFileNameWithoutExtension(file),
                Document: JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidOperationException($"{file} is not a JSON object.")))
            .ToList();
    }

    private static string? Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [Theory]
    [MemberData(nameof(ExpectedCounts))]
    public void EveryArchivedRecordWasImportedExceptTheEmptyOnes(
        string contentType, int archivedRecords, int deliberatelyExcluded)
    {
        Load(contentType).Count.ShouldBe(archivedRecords - deliberatelyExcluded);
    }

    /// <summary>
    /// The file name is the URL. A document whose <c>key</c> disagrees with its
    /// file name resolves under one address and is listed under another, which
    /// is a 404 that only shows up when someone follows a link.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExpectedCounts))]
    public void EveryDocumentsKeyMatchesItsFileName(
        string contentType, int archivedRecords, int deliberatelyExcluded)
    {
        _ = archivedRecords;
        _ = deliberatelyExcluded;

        var mismatched = Load(contentType)
            .Where(entry => Text(entry.Document, "key") != entry.Key)
            .Select(entry => $"{contentType}/{entry.Key}.json declares key '{Text(entry.Document, "key")}'")
            .ToList();

        mismatched.ShouldBeEmpty(string.Join(Environment.NewLine, mismatched));
    }

    /// <summary>
    /// The two facets an enhanced-item list page is unusable without. Nobody
    /// scrolls 1,918 rows, so rarity and item type have to be structured on
    /// every single document rather than on most of them, and the values have
    /// to be the schema's tokens rather than the archive's free text.
    /// </summary>
    [Fact]
    public void EveryEnhancedItemCarriesAFilterableRarityAndItemType()
    {
        var items = Load("enhanced-item");

        items.Count.ShouldBe(1918);

        var rarities = items
            .GroupBy(entry => Text(entry.Document, "rarity"))
            .ToDictionary(group => group.Key ?? "(missing)", group => group.Count());

        // The archive's own distribution. Asserted rather than merely counted,
        // because a mapping that collapsed every item to one rarity would still
        // give 1,918 items each carrying "a rarity".
        rarities.ShouldBe(new Dictionary<string, int>
        {
            ["standard"] = 255,
            ["premium"] = 407,
            ["prototype"] = 365,
            ["advanced"] = 367,
            ["legendary"] = 244,
            ["artifact"] = 280,
        }, ignoreOrder: true);

        var itemTypes = items
            .GroupBy(entry => Text(entry.Document, "itemType"))
            .ToDictionary(group => group.Key ?? "(missing)", group => group.Count());

        itemTypes.ShouldBe(new Dictionary<string, int>
        {
            ["itemModification"] = 1025,
            ["consumable"] = 528,
            ["adventuringGear"] = 156,
            ["weapon"] = 58,
            ["droidCustomization"] = 48,
            ["cyberneticAugmentation"] = 46,
            ["armor"] = 18,
            ["focus"] = 18,
            ["shield"] = 12,
            ["shipArmor"] = 3,
            ["shipShield"] = 3,
            ["shipWeapon"] = 3,
        }, ignoreOrder: true);

        items.Count(entry => entry.Document["requiresAttunement"]?.GetValue<bool>() == true)
             .ShouldBe(133);

        items.Count(entry => Text(entry.Document, "prerequisite") is not null).ShouldBe(157);
    }

    /// <summary>
    /// Every archived prerequisite begins with a stray leading space, and a
    /// third of them lower-case the first word. Both are scrape artefacts of
    /// the same printed clause and both are fixed on import, so no document
    /// should still show either.
    /// </summary>
    [Fact]
    public void EnhancedItemPrerequisitesAreTrimmedAndCapitalised()
    {
        var untidy = Load("enhanced-item")
            .Select(entry => (entry.Key, Prerequisite: Text(entry.Document, "prerequisite")))
            .Where(entry => entry.Prerequisite is not null &&
                            (entry.Prerequisite != entry.Prerequisite.Trim() ||
                             char.IsLower(entry.Prerequisite[0])))
            .Select(entry => $"enhanced-item/{entry.Key}.json: \"{entry.Prerequisite}\"")
            .ToList();

        untidy.ShouldBeEmpty(string.Join(Environment.NewLine, untidy));
    }

    /// <summary>
    /// The four names published in both glossaries with different rules. They
    /// are the reason weapon and armour properties are two content types: one
    /// merged type would have to disambiguate these keys, and could still
    /// answer a lookup for a weapon's "versatile" with the armour rule.
    /// </summary>
    [Fact]
    public void TheTwoPropertyGlossariesOverlapOnFourNamesWithDifferentRules()
    {
        var weapon = Load("weapon-property").ToDictionary(entry => entry.Key, entry => entry.Document);
        var armor = Load("armor-property").ToDictionary(entry => entry.Key, entry => entry.Document);

        weapon.Count.ShouldBe(46);
        armor.Count.ShouldBe(30);

        var shared = weapon.Keys.Intersect(armor.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        shared.ShouldBe(["interlocking", "silent", "strength", "versatile"]);

        foreach (var key in shared)
        {
            Text(armor[key], "description").ShouldNotBe(
                Text(weapon[key], "description"),
                $"'{key}' is published in both glossaries; if the two rules were identical " +
                "these could be one content type after all.");
        }
    }

    /// <summary>
    /// A glossary entry's body must not open by repeating its own name. The
    /// archived text does — every entry starts with a level-four heading naming
    /// the property — and a page that prints its title twice reads as a defect.
    /// </summary>
    [Theory]
    [InlineData("weapon-property")]
    [InlineData("armor-property")]
    public void PropertyDescriptionsDoNotRepeatTheirOwnHeading(string contentType)
    {
        var repeated = Load(contentType)
            .Where(entry =>
            {
                var description = Text(entry.Document, "description") ?? "";
                var name = Text(entry.Document, "name") ?? "";
                var firstLine = description.Split('\n')[0].TrimStart('#', ' ');
                return firstLine.Equals(name, StringComparison.OrdinalIgnoreCase);
            })
            .Select(entry => $"{contentType}/{entry.Key}.json")
            .ToList();

        repeated.ShouldBeEmpty(string.Join(Environment.NewLine, repeated));
    }

    /// <summary>
    /// The rules corpus, by book and kind. Rules are the one content type that
    /// is prose rather than a catalogue, and the four archive files that feed it
    /// are the only record of which book each passage belongs to — so the split
    /// is asserted here, per book, rather than as one total that any mapping
    /// could produce.
    /// </summary>
    [Fact]
    public void TheRulesCorpusIsTheFourArchivedFilesSplitByBookAndKind()
    {
        var rules = Load("rule");

        var byBook = rules
            .GroupBy(entry => $"{Text(entry.Document, "sourceKey")}/{Text(entry.Document, "ruleType")}")
            .ToDictionary(group => group.Key, group => group.Count());

        byBook.ShouldBe(new Dictionary<string, int>
        {
            // The Player's Handbook's sixteenth chapter is its preface, which
            // the archive holds as a title with no text under it.
            ["phb/chapter"] = 15,
            ["wh/chapter"] = 10,
            ["ec/chapter"] = 10,
            ["ec/variant"] = 40,
        }, ignoreOrder: true);

        // Chapters carry a position in their book; variants have none, because
        // they are a menu rather than a sequence.
        rules.Where(entry => Text(entry.Document, "ruleType") == "chapter")
             .ShouldAllBe(entry => entry.Document["chapterNumber"] != null);

        rules.Where(entry => Text(entry.Document, "ruleType") == "variant")
             .ShouldAllBe(entry => entry.Document["chapterNumber"] == null);
    }

    /// <summary>
    /// Seven chapter titles are printed in more than one book, which is why
    /// chapter keys carry the book's key. Without the prefix the later import
    /// would overwrite the earlier one and chapters would vanish from the
    /// corpus with nothing to show for it.
    /// </summary>
    [Fact]
    public void ChapterTitlesRepeatedAcrossBooksGetDistinctKeys()
    {
        var chapters = Load("rule")
            .Where(entry => Text(entry.Document, "ruleType") == "chapter")
            .ToList();

        var repeatedTitles = chapters
            .GroupBy(entry => Text(entry.Document, "name")!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        repeatedTitles.ShouldBe([
            "Changelog",
            "Customization Options",
            "Enhanced Items",
            "Equipment",
            "Introduction",
            "Species",
            "Using Ability Scores",
        ]);

        chapters.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count()
                .ShouldBe(chapters.Count);
    }

    /// <summary>
    /// Rules and reference tables are prose, and their whole value is that a
    /// reader can find a passage in them. A body that survived import as three
    /// words is a body the scrape lost, and it should have been excluded rather
    /// than published.
    /// </summary>
    [Theory]
    [InlineData("rule", "body")]
    [InlineData("reference-table", "body")]
    [InlineData("enhanced-item", "description")]
    public void NoImportedDocumentHasAnEmptyOrTrivialBody(string contentType, string field)
    {
        var trivial = Load(contentType)
            .Where(entry => (Text(entry.Document, field) ?? "").Trim().Length < 10)
            .Select(entry => $"{contentType}/{entry.Key}.json")
            .ToList();

        trivial.ShouldBeEmpty(string.Join(Environment.NewLine, trivial));
    }

    /// <summary>
    /// Line endings and drop caps. The archive is CRLF throughout and opens
    /// several passages with a doubled initial where the source book set a
    /// decorative drop cap; both are scrape artefacts and neither belongs in a
    /// document a contributor is expected to edit by hand.
    /// </summary>
    [Fact]
    public void ImportedProseCarriesNoCarriageReturnsAndNoDropCaps()
    {
        var failures = new List<string>();

        foreach (var contentType in new[] { "enhanced-item", "weapon-property", "armor-property", "rule", "reference-table" })
        {
            foreach (var (key, document) in Load(contentType))
            {
                foreach (var field in new[] { "body", "description" })
                {
                    var text = Text(document, field);

                    if (text is null)
                    {
                        continue;
                    }

                    if (text.Contains('\r'))
                    {
                        failures.Add($"{contentType}/{key}.json: {field} still holds a carriage return.");
                    }

                    var dropCap = System.Text.RegularExpressions.Regex.Match(text, @"(?<=^|\n)([A-Z])\1(?=[a-z])");

                    if (dropCap.Success)
                    {
                        failures.Add(
                            $"{contentType}/{key}.json: {field} opens a line with the doubled " +
                            $"initial \"{text.Substring(dropCap.Index, 12)}\".");
                    }
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Neither property glossary nor the reference tables carry a source, and
    /// that is deliberate: the archive records "None" for all 109 of them and
    /// there is nothing to infer a book from. A citation appearing here would
    /// mean someone had guessed one.
    /// </summary>
    [Theory]
    [InlineData("weapon-property")]
    [InlineData("armor-property")]
    [InlineData("reference-table")]
    public void TypesWithNoArchivedProvenanceCiteNoBook(string contentType)
    {
        Load(contentType).ShouldAllBe(entry => entry.Document["sourceKey"] == null);
    }
}
