using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Sw5e.Database.Schemas;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Guards the curated seed content under <c>content/</c>. The seed set is what
/// the API, the site and every demo are built against, so it has to be valid,
/// free of the archive's corruption, and internally consistent. None of these
/// assertions needs a database.
/// </summary>
public sealed class SeedContentTests
{
    private const char ReplacementCharacter = '\uFFFD';

    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

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
    public void NoSeedFileContainsAReplacementCharacter()
    {
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ContentRoot, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            var index = text.IndexOf(ReplacementCharacter);

            if (index < 0)
            {
                continue;
            }

            var start = Math.Max(0, index - 40);
            var length = Math.Min(text.Length - start, 80);

            failures.Add(
                $"{Relative(file)}: U+FFFD at offset {index}, near \"{text.Substring(start, length)}\". " +
                "The archive lost apostrophes, dashes and accented letters to a bad decode; " +
                "repair the character rather than copying it into the seed set.");
        }

        failures.ShouldBeEmpty(Report("Seed content contains replacement characters:", failures));
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
