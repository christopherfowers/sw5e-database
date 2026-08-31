using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Sw5e.Database.Schemas;
using Xunit;
using Xunit.Abstractions;

namespace Sw5e.Database.Tests;

/// <summary>
/// Holds the v1 content schemas to the corpus they were derived from: every
/// item archived from the legacy API must validate once the documented
/// mechanical mapping has been applied. A schema the real data cannot satisfy
/// is a broken schema, and this is where that shows up.
/// </summary>
public sealed class ArchiveConformanceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Mapping key, the legacy file it is extracted from, and the number of
    /// items that file holds. The count is asserted so a truncated or swapped
    /// archive fails loudly instead of quietly validating a handful of items.
    /// <para>
    /// The mapping key is usually the content type, but three legacy files map
    /// into the single <c>class-improvement</c> type and are told apart by a
    /// suffix, because nothing in those records says which file they came
    /// from. <see cref="LegacyContentMapper.SchemaType"/> takes the key back to
    /// the content type, and so to the schema.
    /// </para>
    /// </summary>
    private static readonly (string MappingKey, string LegacyFile, int ItemCount)[] Corpus =
    [
        ("species", "Species", 141),
        ("background", "Background", 61),
        ("feat", "Feat", 119),
        ("power", "Power", 465),
        ("equipment", "Equipment", 507),
        ("monster", "Monster", 271),
        ("archetype", "Archetype", 137),
        ("feature", "Feature", 2723),

        // The combat options. Six small files rather than one large one,
        // because that is how the archive stores them and how the books print
        // them: a character chooses a fighting style from one list and a
        // fighting mastery from another, and nothing lets one stand in for the
        // other. The counts are the point of writing them down — the whole set
        // is 219 items, and a mapping that quietly produced 40 of them would
        // otherwise look identical to one that worked.
        ("maneuver", "Maneuvers", 119),
        ("fighting-style", "FightingStyle", 32),
        ("fighting-mastery", "FightingMastery", 32),
        ("lightsaber-form", "LightsaberForm", 20),
        ("weapon-focus", "WeaponFocus", 8),
        ("weapon-supremacy", "WeaponSupremacy", 8),

        // The class graph. Three of these mapping keys are not content types:
        // the archive keeps class, multiclass and splashclass improvements in
        // separate files whose records are identical, so the file is the only
        // thing that says which kind a record is.
        ("class", "Class", 10),
        ("class-improvement/class", "ClassImprovement", 10),
        ("class-improvement/multiclass", "MulticlassImprovement", 10),
        ("class-improvement/splashclass", "SplashclassImprovement", 10),

        // The enhanced items and the two glossaries that define what an
        // equipment row means when it prints "burst 2" or "strength 13".
        ("enhanced-item", "EnhancedItem", 1918),
        ("weapon-property", "WeaponProperty", 46),
        ("armor-property", "ArmorProperty", 30),

        // The rules prose. Four more mapping keys that are not content types,
        // for the same reason the class improvements need them: every rules
        // record in the archive has a contentSource of "None", so the file is
        // the only thing that says which book printed the chapter — or that it
        // is not a chapter at all but one of the optional variant rules.
        ("rule/phb", "playerHandbookRule", 16),
        ("rule/wh", "wretchedHivesRule", 10),
        ("rule/ec", "ExpandedContent", 10),
        ("rule/variant", "VariantRule", 40),

        ("reference-table", "ReferenceTable", 33)
    ];

    public static TheoryData<string> MappingKeys
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var entry in Corpus)
            {
                data.Add(entry.MappingKey);
            }

            return data;
        }
    }

    /// <summary>
    /// Items that cannot validate because the archived record is corrupt, not
    /// because the schema is wrong. Each is a repair job for the import stage
    /// rather than a reason to loosen a constraint, so they are named here and
    /// asserted to still fail: if one is repaired upstream, this list goes
    /// stale loudly instead of silently.
    /// </summary>
    /// <remarks>
    /// Keyed by mapping key rather than content type, because four mapping keys
    /// feed the <c>rule</c> type and only one of them holds the empty record.
    /// Keying by type would have the other three expect a corrupt item they do
    /// not have.
    /// </remarks>
    private static readonly Dictionary<(string MappingKey, string Name), string> KnownCorruptItems = new()
    {
        [("monster", "B'omarr Brain Walker")] =
            "challengeRating holds the literal string \"CR\" — the scrape captured the column " +
            "header instead of the value. Its 3 experience points imply a challenge rating of 0.",

        [("species", "Trandoshan")] =
            "Its only image URL ends in \"species_trandoshan (2).png\": the space and parentheses " +
            "were never percent-encoded, so the value is not a valid URI. Re-encode it, or rename " +
            "the blob, during the repair stage.",

        [("rule/phb", "Preface")] =
            "The Player's Handbook preface is a title with an empty body: the scrape captured the " +
            "chapter heading and none of the text under it. There is nothing to publish, so the " +
            "importer does not write a document for it.",

        [("reference-table", "Starship Size Cargo Capacity")] =
            "The caption survived the scrape and the table under it did not, so the record holds " +
            "an empty body. The numbers are unrecoverable from the archive.",

        [("reference-table", "Starship Size Equipment Cost")] =
            "The caption survived the scrape and the table under it did not, so the record holds " +
            "an empty body. The numbers are unrecoverable from the archive.",

        [("reference-table", "Starship Size Equipment Workforce")] =
            "The caption survived the scrape and the table under it did not, so the record holds " +
            "an empty body. The numbers are unrecoverable from the archive."
    };

    [Theory]
    [MemberData(nameof(MappingKeys))]
    public void EverySourceItemValidatesAfterMapping(string mappingKey)
    {
        var archive = LegacyArchive.TryLocate();

        if (archive is null)
        {
            output.WriteLine(LegacyArchive.MissingArchiveMessage);
            return;
        }

        var (_, legacyFile, expectedCount) = Corpus.Single(entry => entry.MappingKey == mappingKey);
        var contentType = LegacyContentMapper.SchemaType(mappingKey);
        var validator = new SchemaValidator(new SchemaRepository(LegacyArchive.SchemaRoot));
        var items = LegacyArchive.Read(archive, legacyFile);

        items.Count.ShouldBe(expectedCount);

        var unexpectedFailures = new List<string>();
        var validated = 0;

        foreach (var item in items)
        {
            var name = DisplayName(item);
            JsonObject mapped;

            try
            {
                mapped = LegacyContentMapper.Map(mappingKey, item);
            }
            catch (Exception exception)
            {
                unexpectedFailures.Add($"{name}: mapping threw {exception.GetType().Name} — {exception.Message}");
                continue;
            }

            var result = validator.Validate(contentType, 1, mapped);

            if (result.IsValid)
            {
                validated++;
                continue;
            }

            if (KnownCorruptItems.ContainsKey((mappingKey, name)))
            {
                continue;
            }

            unexpectedFailures.Add($"{name}: {string.Join("; ", result.Errors.Take(4))}");
        }

        unexpectedFailures.Count.ShouldBe(0, Describe(contentType, unexpectedFailures));

        var knownCorrupt = KnownCorruptItems.Count(entry => entry.Key.MappingKey == mappingKey);

        validated.ShouldBe(expectedCount - knownCorrupt);
        output.WriteLine($"{validated} of {expectedCount} {contentType} items validated against schemas/{contentType}/v1.json.");
    }

    [Fact]
    public void KnownCorruptItemsStillFailValidation()
    {
        var archive = LegacyArchive.TryLocate();

        if (archive is null)
        {
            output.WriteLine(LegacyArchive.MissingArchiveMessage);
            return;
        }

        var validator = new SchemaValidator(new SchemaRepository(LegacyArchive.SchemaRoot));

        foreach (var ((mappingKey, name), reason) in KnownCorruptItems)
        {
            var legacyFile = Corpus.Single(entry => entry.MappingKey == mappingKey).LegacyFile;
            var contentType = LegacyContentMapper.SchemaType(mappingKey);

            var item = LegacyArchive.Read(archive, legacyFile)
                .SingleOrDefault(candidate => DisplayName(candidate) == name);

            item.ShouldNotBeNull($"'{name}' is no longer in the {mappingKey} archive; drop it from KnownCorruptItems.");

            var result = validator.Validate(contentType, 1, LegacyContentMapper.Map(mappingKey, item));

            result.IsValid.ShouldBeFalse(
                $"'{name}' now validates, so it has been repaired upstream. " +
                $"Remove it from KnownCorruptItems. Recorded reason: {reason}");
        }
    }

    [Fact]
    public void EveryAuthoredContentTypeIsDiscoverable()
    {
        var discovered = new SchemaRepository(LegacyArchive.SchemaRoot).ListContentTypes();

        foreach (var (mappingKey, _, _) in Corpus)
        {
            discovered.ShouldContain(LegacyContentMapper.SchemaType(mappingKey));
        }
    }

    /// <summary>
    /// The display name of a record, whichever field the archive keeps it in.
    /// Rule records call it <c>chapterName</c>; everything else calls it
    /// <c>name</c>.
    /// </summary>
    private static string DisplayName(JsonObject item) =>
        LegacyArchive.Text(item, "name") ?? LegacyArchive.Text(item, "chapterName") ?? "(unnamed)";

    private static string Describe(string contentType, IReadOnlyList<string> failures)
    {
        var message = new StringBuilder()
            .AppendLine($"{failures.Count} {contentType} item(s) did not validate against schemas/{contentType}/v1.json.")
            .AppendLine("First failures:");

        foreach (var failure in failures.Take(6))
        {
            message.AppendLine("  - " + failure);
        }

        if (failures.Count > 6)
        {
            message.AppendLine($"  ... and {failures.Count - 6} more.");
        }

        return message.ToString();
    }
}
