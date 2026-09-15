using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Sw5e.Database.Tests;

/// <summary>
/// Locates the byte-exact archive of the legacy SW5e API and reads a content
/// type out of it. The archive is not part of this repository, so every caller
/// has to cope with it being absent.
/// </summary>
public static class LegacyArchive
{
    private const string OverrideVariable = "SW5E_LEGACY_ARCHIVE";

    /// <summary>
    /// Paths tried in order, relative to the repository root, before giving up.
    /// The archive normally sits beside the checkout that contains this repo.
    /// </summary>
    private static readonly string[] RelativeCandidates =
    [
        Path.Combine("..", "..", "sw5e-legacy-archive", "api"),
        Path.Combine("..", "sw5e-legacy-archive", "api"),
        Path.Combine("legacy-archive", "api")
    ];

    public static string RepositoryRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string SchemaRoot { get; } = Path.Combine(RepositoryRoot, "schemas");

    /// <summary>
    /// Absolute path to the archive directory, or null when it is not present
    /// on this machine.
    /// </summary>
    public static string? TryLocate()
    {
        var configured = Environment.GetEnvironmentVariable(OverrideVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Path.GetFullPath(configured);

            return Directory.Exists(expanded) ? expanded : null;
        }

        foreach (var candidate in RelativeCandidates)
        {
            var expanded = Path.GetFullPath(Path.Combine(RepositoryRoot, candidate));

            if (Directory.Exists(expanded))
            {
                return expanded;
            }
        }

        return null;
    }

    public static string MissingArchiveMessage =>
        $"Legacy archive not found. Looked for {string.Join(", ", RelativeCandidates)} " +
        $"relative to {RepositoryRoot}, and at ${OverrideVariable}. " +
        "This assertion only runs where the archive is checked out.";

    /// <summary>
    /// Reads one legacy file, which is always a flat JSON array of objects.
    /// </summary>
    public static IReadOnlyList<JsonObject> Read(string archivePath, string legacyFileName)
    {
        var path = Path.Combine(archivePath, legacyFileName + ".json");
        var parsed = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonArray
            ?? throw new InvalidOperationException($"{path} is not a JSON array.");

        return parsed
            .Select(node => node as JsonObject
                ?? throw new InvalidOperationException($"{path} contains a non-object element."))
            .ToList();
    }

    /// <summary>
    /// Lower-cases the first character only, turning the legacy PascalCase enum
    /// text into the camelCase token the schemas use ("BonusAction" =&gt;
    /// "bonusAction").
    /// </summary>
    public static string CamelCase(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    public static string Lower(string value) => value.ToLowerInvariant();

    /// <summary>
    /// Builds a stable key matching the <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> pattern
    /// the schemas and the schema loader both enforce: lower-case, with every
    /// run of non-alphanumeric characters collapsed to a single hyphen.
    /// </summary>
    public static string Slug(params string?[] parts)
    {
        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            foreach (var character in part)
            {
                if (char.IsAsciiLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }

            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>
    /// Removes every node that carries no information: nulls, blank strings,
    /// empty arrays and empty objects, recursively, so that a container which
    /// becomes empty is itself removed. Numbers and booleans always survive,
    /// including <c>0</c> and <c>false</c>.
    /// </summary>
    public static JsonNode? Prune(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject source:
            {
                var result = new JsonObject();

                foreach (var property in source.ToList())
                {
                    var pruned = Prune(property.Value?.DeepClone());

                    if (pruned is not null)
                    {
                        result[property.Key] = pruned;
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonArray source:
            {
                var result = new JsonArray();

                foreach (var element in source.ToList())
                {
                    var pruned = Prune(element?.DeepClone());

                    if (pruned is not null)
                    {
                        result.Add(pruned);
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return string.IsNullOrWhiteSpace(text) ? null : value.DeepClone();

            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// A string field from the archive, with any table it printed in two
    /// columns rejoined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rejoin belongs here rather than in a pass over <c>content/</c>,
    /// because this is where the archive's text becomes the corpus. Ninety-four
    /// tables were repaired by rewriting the committed documents first, and
    /// <c>CommittedContentIsExactlyWhatTheImportProduces</c> refused it — the
    /// next import would have put every one of them back, and nothing about
    /// that diff would have looked wrong.
    /// </para>
    /// <para>
    /// It runs over every string field, not only the markdown ones, which costs
    /// nothing: <see cref="MarkdownTables.Rejoin"/> returns its input untouched
    /// unless the text holds two adjacent tables with the same header whose
    /// first columns continue one another, and a name or a slug never does.
    /// Applying it everywhere is what makes it impossible for a new field to
    /// quietly miss it.
    /// </para>
    /// </remarks>
    public static string? Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text)
            ? MarkdownTables.Rejoin(text)
            : null;

    public static int? Int(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    public static double? Number(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    public static bool? Bool(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public static JsonArray? Array(JsonObject item, string field) => item[field] as JsonArray;

    public static JsonObject? Object(JsonObject item, string field) => item[field] as JsonObject;

    public static IEnumerable<string> Strings(JsonObject item, string field) =>
        Array(item, field)?
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
        ?? [];

    public static double ParseWeight(string value) =>
        double.Parse(value, CultureInfo.InvariantCulture);
}
