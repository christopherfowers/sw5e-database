using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sw5e.Database.Tools.Legacy;

/// <summary>
/// One document the importer produced, plus where it belongs.
/// </summary>
internal sealed record ImportedDocument(string ContentType, string Key, JsonObject Document);

/// <summary>
/// What one import run did, so the operator sees the shape of the result
/// rather than just an exit code.
/// </summary>
internal sealed class ImportReport
{
    public List<string> Written { get; } = [];

    /// <summary>Archive records deliberately not imported, with the reason.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Documents that still hold a replacement character after repair.</summary>
    public List<string> Unrepaired { get; } = [];
}

/// <summary>
/// Turns the legacy SW5e archive's enhanced-item, property and rules dumps into
/// the canonical content documents this repository maintains.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the pipeline that <em>repairs</em> anything. The
/// content set is meant to be readable and editable by hand, so the corruption
/// is fixed once, here, and never again: no consumer of <c>content/</c> should
/// need to know that the corpus was scraped badly in 2022.
/// </para>
/// <para>
/// It is deliberately re-runnable and deterministic. The same archive produces
/// byte-identical documents, so re-importing after a rule change shows up as a
/// diff of exactly what changed rather than as noise. It writes files and never
/// deletes them, because a document may have been corrected by hand after
/// import and an importer that clears the directory would silently revert that.
/// </para>
/// <para>
/// The mechanical half of this mapping — which archive field becomes which
/// document field — is also written out in the test project's
/// <c>LegacyContentMapper</c>, which applies no repair and is what proves the
/// schemas fit every one of the archive's records. The two agree on structure
/// by construction: if they disagreed, the imported documents would fail the
/// same schema the conformance test validates against.
/// </para>
/// </remarks>
internal static class LegacyImporter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // The corpus is full of apostrophes, and after repair it is full of em
        // dashes and curly quotes too. Escaping those to \uXXXX would make
        // every file unreadable in review for no benefit: the files are UTF-8
        // and nothing reads them but a JSON parser.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Which archive file feeds which content type. Four separate dumps become
    /// one <c>rule</c> type: they carry byte-identical field sets, and the only
    /// thing that distinguishes them is which book the records came from, which
    /// is a value rather than a type.
    /// </summary>
    private static readonly (string ArchiveFile, string SourceKey, string ContentSet)[] RuleFiles =
    [
        ("playerHandbookRule", "phb", "core"),
        ("wretchedHivesRule", "wh", "core"),
        ("ExpandedContent", "ec", "expanded-content"),
    ];

    internal static ImportReport Import(string archiveRoot, string contentRoot)
    {
        var report = new ImportReport();
        var documents = new List<ImportedDocument>();

        documents.AddRange(EnhancedItems(Read(archiveRoot, "EnhancedItem"), report));
        documents.AddRange(Properties(
            Read(archiveRoot, "WeaponProperty"), "weapon-property", report));
        documents.AddRange(Properties(
            Read(archiveRoot, "ArmorProperty"), "armor-property", report));
        documents.AddRange(RuleChapters(archiveRoot, report));
        documents.AddRange(VariantRules(Read(archiveRoot, "VariantRule"), report));
        documents.AddRange(ReferenceTables(Read(archiveRoot, "ReferenceTable"), report));

        GuardAgainstDuplicateKeys(documents);

        foreach (var document in documents)
        {
            var directory = Path.Combine(contentRoot, document.ContentType);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, document.Key + ".json");
            var json = JsonSerializer.Serialize(document.Document, WriteOptions);

            File.WriteAllText(path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            report.Written.Add($"{document.ContentType}/{document.Key}.json");

            var losses = LegacyText.UnrepairedCount(json);

            if (losses > 0)
            {
                report.Unrepaired.Add(
                    $"{document.ContentType}/{document.Key}.json: {losses} character(s)");
            }
        }

        return report;
    }

    /// <summary>
    /// Two documents of the same type cannot share a key: the key is the file
    /// name, so a collision would mean one silently overwriting the other and
    /// a document vanishing from the corpus with nothing to show for it.
    /// </summary>
    private static void GuardAgainstDuplicateKeys(IReadOnlyList<ImportedDocument> documents)
    {
        var collisions = documents
            .GroupBy(document => (document.ContentType, document.Key))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.ContentType}/{group.Key.Key} ({group.Count()} documents)")
            .ToList();

        if (collisions.Count > 0)
        {
            throw new InvalidOperationException(
                "The import produced colliding keys, which would drop documents on write: " +
                string.Join(", ", collisions));
        }
    }

    // ---------------------------------------------------------- enhanced items

    /// <summary>
    /// The legacy record carries ten mutually exclusive <c>*Type</c> fields —
    /// one per kind of enhanced item — of which at most one is ever set, and
    /// which for over half the corpus are all "None" even when the item plainly
    /// has a kind. The <c>subtype</c> field says the same thing for every
    /// record and says it more precisely, so it is the one that is kept and the
    /// ten discriminators are dropped.
    /// </summary>
    private static IEnumerable<ImportedDocument> EnhancedItems(
        JsonArray records, ImportReport report)
    {
        foreach (var item in records.OfType<JsonObject>())
        {
            var name = Text(item, "name")!;
            var description = LegacyText.Repair(Text(item, "text"));

            if (description is null)
            {
                report.Skipped.Add($"enhanced-item '{name}': no rules text survives repair.");
                continue;
            }

            var document = new JsonObject
            {
                ["key"] = LegacyText.Slug(name),
                ["name"] = name,
                ["sourceKey"] = Text(item, "contentSource")!.ToLowerInvariant(),
                ["contentSet"] = ContentSet(Text(item, "contentType")),
                ["itemType"] = CamelCase(Text(item, "type")!),
                ["rarity"] = Rarity(item),
                ["requiresAttunement"] = item["requiresAttunement"]!.GetValue<bool>(),
            };

            if (Subtype(Text(item, "subtype")) is { } subtype)
            {
                document["subtype"] = subtype;
            }

            if (Prerequisite(Text(item, "prerequisite")) is { } prerequisite)
            {
                document["prerequisite"] = prerequisite;
            }

            document["description"] = description;

            yield return new ImportedDocument("enhanced-item", (string)document["key"]!, document);
        }
    }

    /// <summary>
    /// The archive stores rarity twice as an array and twice as a string. Every
    /// one of the 1,918 records has exactly one rarity, so the array is
    /// collapsed to a scalar. <c>rarityOptions</c> is preferred over
    /// <c>rarityText</c>, which is inconsistently cased — eleven records
    /// capitalise it and the rest do not — and over <c>searchableRarity</c>,
    /// which is a display artefact of the old site's search box.
    /// </summary>
    private static string Rarity(JsonObject item)
    {
        var options = (item["rarityOptions"] as JsonArray)?
            .OfType<JsonValue>()
            .Select(value => value.GetValue<string>())
            .ToList() ?? [];

        if (options.Count != 1)
        {
            throw new InvalidOperationException(
                $"'{Text(item, "name")}' records {options.Count} rarities; the schema models one. " +
                "A record with several would need a rarity array and a decision about what a " +
                "list page sorts on, so it fails here rather than picking one.");
        }

        return options[0].ToLowerInvariant();
    }

    /// <summary>
    /// Normalises the free-text subtype. The archive is inconsistent in two
    /// small ways and consistent in every other: three item modifications
    /// capitalise "Lightweapon" where a hundred and nine do not, and one
    /// adventuring-gear entry writes the singular "forearm" where the other
    /// writes "forearms" — as every other paired body slot in the list does,
    /// "hands", "legs", "shoulders". Fifty adventuring-gear entries record no
    /// subtype at all, which stays absent rather than becoming an empty string.
    /// </summary>
    private static string? Subtype(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed == "forearm" ? "forearms" : trimmed;
    }

    /// <summary>
    /// Every archived prerequisite begins with a stray leading space, and a
    /// third of them lower-case the first word where the rest capitalise it
    /// ("at least 3 levels in fighter" against "At least 3 levels in fighter").
    /// Both are scrape artefacts of the same printed clause, so the space goes
    /// and the first letter is raised.
    /// </summary>
    private static string? Prerequisite(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    // ------------------------------------------------------------- properties

    /// <summary>
    /// Weapon and armour properties differ only in which glossary they are
    /// printed in, so one mapping serves both. Neither carries a source: the
    /// archive records <c>contentSource</c> as "None" for all seventy-six, and
    /// the file they are in names the kind of property rather than a book, so
    /// there is nothing to attribute them to. Guessing a book would put a false
    /// citation on a rules page, which is worse than leaving the field out.
    /// </summary>
    private static IEnumerable<ImportedDocument> Properties(
        JsonArray records, string contentType, ImportReport report)
    {
        foreach (var item in records.OfType<JsonObject>())
        {
            var name = Text(item, "name")!;
            var body = LegacyText.Repair(Text(item, "content"));

            if (body is null)
            {
                report.Skipped.Add($"{contentType} '{name}': no rules text survives repair.");
                continue;
            }

            var document = new JsonObject
            {
                ["key"] = LegacyText.Slug(name),
                ["name"] = name,
                ["contentSet"] = ContentSet(Text(item, "contentType")),
                ["description"] = LegacyText.StripLeadingHeadingMatching(body, name),
            };

            yield return new ImportedDocument(contentType, (string)document["key"]!, document);
        }
    }

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// The three book files, imported as chapters.
    /// </summary>
    /// <remarks>
    /// Chapter keys carry the book's key as a prefix. Seven chapter titles are
    /// reused across the books — all three print a chapter called "Equipment",
    /// and "Customization Options", "Using Ability Scores", "Introduction",
    /// "Changelog", "Enhanced Items" and "Species" each appear in two — so an
    /// unprefixed key would silently drop whichever book was imported first.
    /// </remarks>
    private static IEnumerable<ImportedDocument> RuleChapters(
        string archiveRoot, ImportReport report)
    {
        foreach (var (archiveFile, sourceKey, contentSet) in RuleFiles)
        {
            foreach (var item in Read(archiveRoot, archiveFile).OfType<JsonObject>())
            {
                var name = Text(item, "chapterName")!;
                var body = LegacyText.Repair(Text(item, "contentMarkdown"));

                if (body is null)
                {
                    // The Player's Handbook preface is the only one of these:
                    // the archived record has a title and an empty body. There
                    // is nothing to publish, and a page carrying a heading and
                    // no text would read as a site defect rather than as an
                    // absence in the source.
                    report.Skipped.Add(
                        $"rule '{name}' ({sourceKey}): the archived chapter has no text.");
                    continue;
                }

                var document = new JsonObject
                {
                    ["key"] = LegacyText.Slug(sourceKey, name),
                    ["name"] = name,
                    ["sourceKey"] = sourceKey,
                    ["contentSet"] = contentSet,
                    ["ruleType"] = "chapter",
                };

                if (item["chapterNumber"] is JsonValue number &&
                    number.TryGetValue<int>(out var chapterNumber))
                {
                    document["chapterNumber"] = chapterNumber;
                }

                document["body"] = LegacyText.StripLeadingHeadingMatching(body, name);

                yield return new ImportedDocument("rule", (string)document["key"]!, document);
            }
        }
    }

    /// <summary>
    /// The forty optional variant rules. They are attributed to the Expanded
    /// Content supplement because that is the book whose "Variant Rules"
    /// chapter prints them — the archive marks every one of them as expanded
    /// content and records no source of its own.
    /// </summary>
    private static IEnumerable<ImportedDocument> VariantRules(
        JsonArray records, ImportReport report)
    {
        foreach (var item in records.OfType<JsonObject>())
        {
            var name = Text(item, "chapterName")!;
            var body = LegacyText.Repair(Text(item, "contentMarkdown"));

            if (body is null)
            {
                report.Skipped.Add($"rule '{name}' (variant): the archived rule has no text.");
                continue;
            }

            var document = new JsonObject
            {
                ["key"] = LegacyText.Slug(name),
                ["name"] = name,
                ["sourceKey"] = "ec",
                ["contentSet"] = "expanded-content",
                ["ruleType"] = "variant",
                ["body"] = LegacyText.StripLeadingHeadingMatching(body, name),
            };

            yield return new ImportedDocument("rule", (string)document["key"]!, document);
        }
    }

    // -------------------------------------------------------- reference tables

    /// <summary>
    /// Keywords that place a table under a subject, tried in order. The first
    /// match wins, so the starship terms are listed first: "Modification
    /// Capacity by Ship Size" is a starship table and would otherwise be caught
    /// by nothing at all.
    /// </summary>
    private static readonly (string Keyword, string Subject)[] TableSubjects =
    [
        ("starship", "Starships"),
        ("ship size", "Starships"),
        ("ship tier", "Starships"),
        ("hyperspace", "Starships"),
        ("realspace", "Starships"),
        ("deployment", "Starships"),
        ("by tier", "Starships"),
        ("modification", "Starships"),
        ("ability score", "Character creation"),
        ("multiclassing", "Character creation"),
        ("xp and pb", "Character creation"),
        ("lifestyle", "Downtime"),
        ("slowed", "Conditions"),
    ];

    /// <summary>
    /// The standalone lookup tables. Like the properties, these carry no source
    /// in the archive, and unlike the rule chapters there is no file name to
    /// infer one from: the thirty-three come from at least three different
    /// books. They are published without a citation rather than with a wrong
    /// one.
    /// </summary>
    private static IEnumerable<ImportedDocument> ReferenceTables(
        JsonArray records, ImportReport report)
    {
        foreach (var item in records.OfType<JsonObject>())
        {
            var name = Text(item, "name")!;
            var body = LegacyText.Repair(Text(item, "content"));

            if (body is null)
            {
                // Three starship tables are captions with no table under them.
                report.Skipped.Add($"reference-table '{name}': the archived table is empty.");
                continue;
            }

            var document = new JsonObject
            {
                ["key"] = LegacyText.Slug(name),
                ["name"] = name,
                ["contentSet"] = ContentSet(Text(item, "contentType")),
                ["body"] = body,
            };

            if (Subject(name) is { } subject)
            {
                document["subject"] = subject;
            }

            yield return new ImportedDocument("reference-table", (string)document["key"]!, document);
        }
    }

    private static string? Subject(string name)
    {
        foreach (var (keyword, subject) in TableSubjects)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return subject;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The archive's content set, which four of the eight files in this
    /// importer's scope leave as "None". Wretched Hives is the case that
    /// matters: its rule chapters record "None" while its 1,550 enhanced items
    /// record "Core", and they are the same book, so "None" resolves to core.
    /// </summary>
    private static string ContentSet(string? value) => value switch
    {
        "ExpandedContent" => "expanded-content",
        _ => "core",
    };

    /// <summary>
    /// Lower-cases the first character only, turning legacy PascalCase enum
    /// text into the camelCase token the schemas use ("ItemModification" =&gt;
    /// "itemModification").
    /// </summary>
    private static string CamelCase(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string? Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonArray Read(string archiveRoot, string fileName)
    {
        var path = Path.Combine(archiveRoot, fileName + ".json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The legacy archive has no {fileName}.json. Point the importer at the " +
                "directory holding the archived API dumps.", path);
        }

        return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonArray
            ?? throw new InvalidOperationException($"{path} is not a JSON array.");
    }
}
