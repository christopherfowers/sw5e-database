using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Sw5e.Database.Schemas;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Guards the content set under <c>content/</c>. It is what the API, the site
/// and every demo are built against, so it has to be valid, free of the
/// archive's corruption, and internally consistent. None of these assertions
/// needs a database.
/// </summary>
public sealed class SeedContentTests
{
    private const char ReplacementCharacter = '\uFFFD';

    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    /// <summary>
    /// How many documents each content type publishes. These are exact because
    /// the failure this guards against is the content set silently collapsing
    /// back to a handful of samples per type, which renders as a site that
    /// looks broken rather than as an error. "More than zero" would not catch
    /// it.
    /// <para>
    /// Where a count is below the number of records in the legacy archive, the
    /// archive holds more than one row for a single item and the difference is
    /// explained here:
    /// </para>
    /// <list type="bullet">
    /// <item>feat: 119 archived rows, 118 documents. "Fighting Styles and
    /// Masteries" is printed in Wretched Hives and reprinted unchanged in the
    /// Expanded Content supplement; only the provenance differs.</item>
    /// <item>equipment: 507 archived rows, 505 documents. A bo-rifle and a
    /// saberstaff each belong to two weapon proficiency groups and are printed
    /// once per group. Each is published once, carrying both groups.</item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedDocumentCounts = new()
    {
        ["species"] = 141,
        ["background"] = 61,
        ["feat"] = 118,
        ["power"] = 465,
        ["equipment"] = 505,
        ["monster"] = 271
    };

    /// <summary>
    /// Every character the archive lost that cannot be recovered, by file.
    /// <para>
    /// These are the two shapes <c>repair-text.mjs</c> in the site repository
    /// documents as deliberately unrepaired: an accented letter inside a
    /// species name table, where the letter is gone and inventing one would
    /// fabricate game content; and a lost character before a space, which is
    /// ambiguous between an em dash and an ellipsis with nothing in the
    /// context to choose between them.
    /// </para>
    /// <para>
    /// The counts are exact in both directions. New damage fails, and so does
    /// a repair, which is what forces this list to be revisited rather than
    /// left to rot.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> UnrepairableCharacters = new(StringComparer.Ordinal)
    {
        ["content/background/city-watch.json"] = 1,
        ["content/background/courtier.json"] = 2,
        ["content/background/faction-artisan.json"] = 2,
        ["content/background/faction-merchant.json"] = 2,
        ["content/background/scoundrel.json"] = 1,
        ["content/background/soldier.json"] = 1,
        ["content/background/un-retired-adventurer.json"] = 1,
        ["content/background/urchin.json"] = 1,
        ["content/species/kalleran.json"] = 3,
        ["content/species/kiffar.json"] = 1,
        ["content/species/massassi.json"] = 2,
        ["content/species/theelin.json"] = 2
    };

    /// <summary>
    /// Every seed file, as (content type, path, parsed document). The content
    /// type is the directory the file sits in, matching the schema directory.
    /// </summary>
    private static IReadOnlyList<(string ContentType, string Path, JsonObject Document)> LoadSeedContent()
    {
        Directory.Exists(ContentRoot).ShouldBeTrue(
            $"No seed content directory at '{ContentRoot}'.");

        var files = Directory
            .EnumerateFiles(ContentRoot, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();

        files.ShouldNotBeEmpty($"No seed content files found under '{ContentRoot}'.");

        return files
            .Select(file =>
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var document = JsonNode.Parse(text) as JsonObject
                    ?? throw new InvalidOperationException($"{file} is not a JSON object.");

                return (ContentType: Path.GetFileName(Path.GetDirectoryName(file)) ?? "",
                        Path: Relative(file),
                        Document: document);
            })
            .ToList();
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(LegacyArchive.RepositoryRoot, path).Replace('\\', '/');

    private static string Report(string headline, IReadOnlyCollection<string> failures) =>
        $"{headline}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";

    private static string? Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [Fact]
    public void EverySeedFileValidatesAgainstItsSchema()
    {
        var validator = new SchemaValidator(new SchemaRepository(LegacyArchive.SchemaRoot));
        var failures = new List<string>();

        foreach (var (contentType, path, document) in LoadSeedContent())
        {
            SchemaValidationResult result;

            try
            {
                result = validator.Validate(contentType, 1, document);
            }
            catch (SchemaNotFoundException)
            {
                failures.Add($"{path}: no schema for content type '{contentType}' version 1.");
                continue;
            }

            if (!result.IsValid)
            {
                failures.AddRange(result.Errors.Select(error => $"{path}: {error}"));
            }
        }

        failures.ShouldBeEmpty(Report("Seed content failed schema validation:", failures));
    }

    [Fact]
    public void EveryContentTypePublishesItsWholeCorpus()
    {
        var actual = Directory
            .EnumerateDirectories(ContentRoot)
            .ToDictionary(
                directory => Path.GetFileName(directory)!,
                directory => Directory.EnumerateFiles(directory, "*.json").Count());

        var failures = new List<string>();

        foreach (var (contentType, expected) in ExpectedDocumentCounts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(contentType, out var count))
            {
                failures.Add($"content/{contentType}/ is missing entirely; expected {expected} documents.");
                continue;
            }

            if (count != expected)
            {
                failures.Add(
                    $"content/{contentType}/ holds {count} document(s), expected {expected}. " +
                    "If the corpus really has changed, update ExpectedDocumentCounts and say why " +
                    "in the same commit.");
            }
        }

        failures.ShouldBeEmpty(Report("Content type counts have drifted:", failures));
    }

    /// <summary>
    /// The archive lost apostrophes, dashes and accented letters to a bad
    /// decode. Almost all of it is repairable from context and is repaired
    /// before the content is written; what is left is enumerated exactly, so
    /// neither new damage nor a silent repair can pass unnoticed.
    /// </summary>
    [Fact]
    public void OnlyTheUnrepairableCharactersRemain()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ContentRoot, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            var count = text.Count(character => character == ReplacementCharacter);

            if (count == 0)
            {
                continue;
            }

            var path = Relative(file);
            found[path] = count;

            if (!UnrepairableCharacters.TryGetValue(path, out var expected))
            {
                var index = text.IndexOf(ReplacementCharacter);
                var start = Math.Max(0, index - 40);
                var length = Math.Min(text.Length - start, 80);

                failures.Add(
                    $"{path}: {count} replacement character(s), none expected. First is at offset " +
                    $"{index}, near \"{text.Substring(start, length)}\". Repair it rather than " +
                    "copying the damage into the content set; if it truly cannot be recovered, " +
                    "add it to UnrepairableCharacters with the reason.");
            }
            else if (count != expected)
            {
                failures.Add(
                    $"{path}: {count} replacement character(s), expected {expected}.");
            }
        }

        foreach (var (path, expected) in UnrepairableCharacters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!found.ContainsKey(path))
            {
                failures.Add(
                    $"{path}: expected {expected} unrepairable character(s) and found none. " +
                    "If the text has been repaired or the file removed, drop the entry from " +
                    "UnrepairableCharacters.");
            }
        }

        failures.ShouldBeEmpty(Report("Replacement characters do not match the recorded inventory:", failures));
    }

    [Fact]
    public void EveryCrossReferenceResolvesWithinTheSeedSet()
    {
        var content = LoadSeedContent();

        var byType = content
            .GroupBy(entry => entry.ContentType)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<(string ContentType, string Path, JsonObject Document)> Items(string contentType) =>
            byType.TryGetValue(contentType, out var items) ? items : [];

        var sourceKeys = Items("source")
            .Select(entry => Text(entry.Document, "key")!)
            .ToHashSet(StringComparer.Ordinal);

        var featNames = Items("feat")
            .Select(entry => Text(entry.Document, "name")!)
            .ToHashSet(StringComparer.Ordinal);

        var speciesNames = Items("species")
            .Select(entry => Text(entry.Document, "name")!)
            .ToHashSet(StringComparer.Ordinal);

        var archetypeNames = Items("archetype")
            .Select(entry => Text(entry.Document, "name")!)
            .ToHashSet(StringComparer.Ordinal);

        // Classes are not a content type of their own yet, so the archetypes are
        // the seed set's only record of which classes exist.
        var classNames = Items("archetype")
            .Select(entry => Text(entry.Document, "className")!)
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();

        void Require(bool resolved, string path, string field, string value, string target)
        {
            if (!resolved)
            {
                failures.Add($"{path}: {field} references {target} '{value}', which is not in the seed set.");
            }
        }

        foreach (var (contentType, path, document) in content)
        {
            if (Text(document, "sourceKey") is { } sourceKey)
            {
                Require(sourceKeys.Contains(sourceKey), path, "sourceKey", sourceKey, "source");
            }

            switch (contentType)
            {
                case "background":
                    foreach (var option in document["featOptions"] as JsonArray ?? [])
                    {
                        if (option is JsonObject row && Text(row, "name") is { } featName)
                        {
                            Require(featNames.Contains(featName), path, "featOptions", featName, "feat");
                        }
                    }

                    break;

                case "feat":
                    // Prerequisites name other feats as "<Name> feat", e.g.
                    // Tough's "4th level, Durable feat".
                    foreach (var required in PrerequisiteFeats(Text(document, "prerequisite")))
                    {
                        Require(featNames.Contains(required), path, "prerequisite", required, "feat");
                    }

                    break;

                case "feature":
                    var grantedByName = Text(document, "grantedByName")!;

                    switch (Text(document, "grantedBy"))
                    {
                        case "archetype":
                            Require(archetypeNames.Contains(grantedByName), path,
                                "grantedByName", grantedByName, "archetype");
                            break;

                        case "species":
                            Require(speciesNames.Contains(grantedByName), path,
                                "grantedByName", grantedByName, "species");
                            break;

                        case "class":
                            Require(classNames.Contains(grantedByName), path,
                                "grantedByName", grantedByName, "class named by an archetype");
                            break;
                    }

                    break;

                case "species":
                    foreach (var entry in document["halfHumanTraits"] as JsonArray ?? [])
                    {
                        if (entry is JsonObject half && Text(half, "speciesName") is { } parent)
                        {
                            Require(speciesNames.Contains(parent), path,
                                "halfHumanTraits", parent, "species");
                        }
                    }

                    break;
            }
        }

        failures.ShouldBeEmpty(Report("Seed content has dangling cross-references:", failures));
    }

    /// <summary>
    /// Pulls the feat names out of a prerequisite line. A prerequisite is a
    /// comma-separated list of clauses, and a clause that names another feat
    /// always ends in the word "feat".
    /// </summary>
    private static IEnumerable<string> PrerequisiteFeats(string? prerequisite)
    {
        if (string.IsNullOrWhiteSpace(prerequisite))
        {
            yield break;
        }

        foreach (var clause in prerequisite.Split([',', ';'], StringSplitOptions.TrimEntries))
        {
            if (clause.EndsWith(" feat", StringComparison.Ordinal))
            {
                yield return clause[..^" feat".Length].Trim();
            }
        }
    }
}
