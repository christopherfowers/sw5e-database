using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sw5e.Database.Tests;

/// <summary>One imported document and where in <c>content/</c> it belongs.</summary>
public sealed record ImportedDocument(string ContentType, string Key, JsonObject Document);

/// <summary>
/// The import stage for the class graph: classes, their three improvement
/// rules, their archetypes, and the features any of those grant.
/// </summary>
/// <remarks>
/// <para>
/// This is the stage <see cref="LegacyContentMapper"/> deliberately stops short
/// of. Mapping is mechanical and lossless; importing is where the corruption in
/// a 2022 PDF scrape gets dealt with, and where the handful of records that no
/// rule can fix are named one at a time.
/// </para>
/// <para>
/// It runs in four passes over each mapped document, in this order:
/// </para>
/// <list type="number">
/// <item>repair every string and drop the ones that repair to nothing, along
/// with any array or object that leaves empty;</item>
/// <item>apply the hand-adjudicated corrections below, each of which names one
/// document and one reason;</item>
/// <item>drop table cells that lost their contents, because a heading with
/// nothing under it is not a cell;</item>
/// <item>nothing else — no reordering, no rewriting, no inference.</item>
/// </list>
/// <para>
/// The result is deterministic: the same archive produces the same bytes.
/// <see cref="ImportedContentTests"/> depends on that, because it asserts that
/// every committed file in these four directories is exactly what this
/// produces, which is what makes the corpus reviewable — a diff on
/// <c>content/</c> is a diff on the archive plus a named judgement, never an
/// unexplained edit.
/// </para>
/// </remarks>
public static class LegacyContentImport
{
    /// <summary>
    /// How content files are written: two-space indentation, and the relaxed
    /// encoder so an apostrophe stays an apostrophe rather than becoming
    /// <c>'</c>. These files are read and edited by hand.
    /// </summary>
    /// <remarks>
    /// The line ending is pinned to a bare newline rather than left to the
    /// platform, so an import run on Windows and one on the CI runner produce
    /// the same bytes. What lands in a working tree after that is git's
    /// business; the import does not get a vote.
    /// </remarks>
    public static readonly JsonSerializerOptions FileFormat = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The content types this import owns, in the order a reviewer reads them:
    /// the class, what it is worth to a character who is not advancing in it,
    /// what it branches into, and what any of that grants.
    /// </summary>
    public static readonly string[] ContentTypes =
        ["class", "class-improvement", "archetype", "feature"];

    /// <summary>
    /// Which feature grants this import is responsible for: all of them.
    /// </summary>
    /// <remarks>
    /// A third of this corpus — 1,593 of 2,682 features — is granted by a
    /// species rather than by a class or an archetype. They were held back
    /// while <c>content/species</c> was a fourteen-item sample, because every
    /// one of them names its species in <c>grantedByName</c> and the seed set's
    /// cross-reference guard requires that name to resolve; importing them then
    /// would have meant either a red build or a weakened guard, and a guard
    /// that tolerates dangling references is not a guard. All 141 species are
    /// now published, so they resolve and they are imported.
    /// </remarks>
    private static readonly string[] FeatureGrantKinds = ["Class", "Archetype", "Species"];

    /// <summary>
    /// Every document this import produces, ordered by content type and then by
    /// key so a caller can diff two runs line for line.
    /// </summary>
    public static IReadOnlyList<ImportedDocument> Run(string archivePath)
    {
        var documents = new List<ImportedDocument>();

        documents.AddRange(Import("class", LegacyArchive.Read(archivePath, "Class"), "class"));

        // One content type from three files. Nothing in an improvement record
        // says which of the three it came from, so the file is what names the
        // kind, and the mapping key carries it.
        documents.AddRange(Import("class-improvement",
            LegacyArchive.Read(archivePath, "ClassImprovement"), "class-improvement/class"));
        documents.AddRange(Import("class-improvement",
            LegacyArchive.Read(archivePath, "MulticlassImprovement"), "class-improvement/multiclass"));
        documents.AddRange(Import("class-improvement",
            LegacyArchive.Read(archivePath, "SplashclassImprovement"), "class-improvement/splashclass"));

        documents.AddRange(Import("archetype", LegacyArchive.Read(archivePath, "Archetype"), "archetype"));

        var features = Import("feature", Features(archivePath), "feature").ToList();
        AttributeFeatures(features, documents);
        documents.AddRange(features);

        return [.. documents
            .OrderBy(document => Array.IndexOf(ContentTypes, document.ContentType))
            .ThenBy(document => document.Key, StringComparer.Ordinal)];
    }

    private static IEnumerable<ImportedDocument> Import(
        string contentType,
        IEnumerable<JsonObject> records,
        string mappingKey) =>
        records.Select(record =>
        {
            var mapped = LegacyContentMapper.Map(mappingKey, record);
            var document = LegacyTextRepair.RepairDocument(mapped) as JsonObject
                ?? throw new InvalidOperationException(
                    $"A {contentType} record repaired to nothing at all, which cannot happen: " +
                    "every one of them carries at least a name.");

            var key = LegacyArchive.Text(document, "key")
                ?? throw new InvalidOperationException($"A mapped {contentType} document has no key.");

            Adjudicate(contentType, key, document);
            DropCellsEmptiedByRepair(document);

            return new ImportedDocument(contentType, key, document);
        });

    /// <summary>
    /// The feature records this import is responsible for, with the archive's
    /// duplicate rows resolved.
    /// </summary>
    /// <remarks>
    /// Forty-one archetype features appear twice in the dump, under the same
    /// row key: the scrape captured two revisions of the same row. Thirty-three
    /// of those pairs are identical and the choice does not matter; the other
    /// eight differ, and in all eight the later timestamp is the text the
    /// parent archetype's own page prints, so the newest row wins and the older
    /// revision is dropped. That takes 2,723 rows to 2,682: 218 granted by a
    /// class, 871 by an archetype and 1,593 by a species.
    /// </remarks>
    private static IEnumerable<JsonObject> Features(string archivePath) =>
        LegacyArchive.Read(archivePath, "Feature")
            .Where(record => FeatureGrantKinds.Contains(LegacyArchive.Text(record, "source")))
            .GroupBy(record => LegacyArchive.Slug(
                LegacyArchive.Text(record, "source"),
                LegacyArchive.Text(record, "sourceName"),
                LegacyArchive.Text(record, "name"),
                LegacyArchive.Int(record, "level")?.ToString()), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(record => LegacyArchive.Text(record, "timestamp"), StringComparer.Ordinal)
                .First());

    /// <summary>
    /// The published species documents, read from <c>content/species</c>.
    /// </summary>
    private static IEnumerable<JsonObject> PublishedSpecies()
    {
        var directory = Path.Combine(LegacyArchive.RepositoryRoot, "content", "species");

        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            if (JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) is JsonObject document)
            {
                yield return document;
            }
        }
    }

    /// <summary>
    /// Gives every feature the provenance of whatever grants it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A feature record in the archive carries no source and no content set at
    /// all — unlike every other type, whose provenance is copied straight
    /// across. It does carry a storage partition that looks like one, and using
    /// it would be wrong: the partition disagrees with the granting entry for
    /// 47 of these 1,089 features, and the partition is the side that must be
    /// wrong, because a feature is printed inside the class or archetype that
    /// grants it. An Ataru Form feature cannot be in the Player's Handbook when
    /// Ataru Form itself is in Expanded Content.
    /// </para>
    /// <para>
    /// This matters beyond a badge. The site refuses to render an item whose
    /// source it cannot resolve, on the grounds that a library with its
    /// provenance quietly stripped off hides a disagreement between the content
    /// set and the mapping. Without this pass, no feature could be published at
    /// all.
    /// </para>
    /// </remarks>
    private static void AttributeFeatures(
        IEnumerable<ImportedDocument> features,
        IEnumerable<ImportedDocument> grantors)
    {
        var provenance = grantors
            .Where(document => document.ContentType is "class" or "archetype")
            .ToDictionary(
                document => (document.ContentType, LegacyArchive.Text(document.Document, "name")!),
                document => (Source: LegacyArchive.Text(document.Document, "sourceKey")!,
                             Set: LegacyArchive.Text(document.Document, "contentSet")!));

        // Species are somebody else's import. Their provenance is read from
        // the published documents rather than re-derived from the archive,
        // because those documents are the authority on what this repository
        // says a species is — and because a species that is not there has to
        // fail here rather than produce a feature nothing can attribute.
        foreach (var species in PublishedSpecies())
        {
            provenance[("species", LegacyArchive.Text(species, "name")!)] =
                (LegacyArchive.Text(species, "sourceKey")!, LegacyArchive.Text(species, "contentSet")!);
        }

        foreach (var feature in features)
        {
            var grantor = (LegacyArchive.Text(feature.Document, "grantedBy")!,
                           LegacyArchive.Text(feature.Document, "grantedByName")!);

            if (!provenance.TryGetValue(grantor, out var inherited))
            {
                throw new InvalidOperationException(
                    $"'{feature.Key}' is granted by {grantor.Item1} '{grantor.Item2}', which this " +
                    "import did not produce, so its provenance cannot be established.");
            }

            feature.Document["sourceKey"] = inherited.Source;
            feature.Document["contentSet"] = inherited.Set;
        }
    }

    // ----------------------------------------------------------- adjudication

    /// <summary>
    /// A correction to one named document that no rule can make, recorded with
    /// the reason it is defensible. Each one is asserted to fire, so an archive
    /// that gets fixed upstream makes this list go stale loudly rather than
    /// quietly.
    /// </summary>
    private sealed record Adjudication(
        string ContentType,
        string Key,
        string Reason,
        Func<JsonObject, bool> Apply);

    private static readonly Adjudication[] Adjudications =
    [
        new("archetype", "trickster-order",
            "The only replacement character left in this corpus after repair. It sits between " +
            "a closing quotation mark and a space — 'the archetypal role of the \"trickster\"? " +
            "one who seemingly bumbles through life' — where the general rules refuse to choose " +
            "between an em dash and an ellipsis. Here the sentence continues into an appositive " +
            "that renames the quoted word, which an ellipsis does not introduce and a dash does.",
            document => ReplaceIn(document, "description",
                "\"trickster\"� one", "\"trickster\"— one")),

        new("class", "sentinel",
            "The Features cell of the sentinel's 9th-level row holds the bare digit 3, which " +
            "cannot be the name of anything. The row's own Ideals Known and Ideal Manifests " +
            "columns both read 3, so the scrape picked up a neighbouring cell; the levels either " +
            "side of it that grant nothing print an em dash. The cell is dropped rather than " +
            "guessed at, which leaves the row saying nothing arrives at 9th level — and the " +
            "authoritative record of what does is the feature documents, which put Sentinel " +
            "Ideals at 9th level regardless of what the table prints.",
            document => RemoveFeature(document, level: 9, printed: "3")),

        // The next three preserve corrections that were already made, by hand,
        // in the six archetypes and fifteen features this import replaces. An
        // import that reproduced the archive faithfully would quietly undo a
        // reviewed edit, which is the one kind of regression a bulk import is
        // most likely to smuggle in. Each is repeated wherever the same
        // sentence appears, because an archetype's page and the feature
        // extracted from it hold the same words.
        new("archetype", "beguiler-practice",
            "The source misspells \"attempting\". Corrected in the reviewed seed set before " +
            "this import existed; kept corrected here.",
            document => ReplaceIn(document, "description", "attemping", "attempting")),

        new("feature", "archetype-beguiler-practice-fascinating-display-3",
            "The same misspelling, in the feature extracted from the same paragraph.",
            document => ReplaceIn(document, "description", "attemping", "attempting")),

        new("archetype", "way-of-lightning",
            "The source types an em dash as two hyphens in one sentence. Corrected in the " +
            "reviewed seed set. This is not generalised into a rule: the other hundred-odd " +
            "double hyphens in this corpus are all markdown table rules, and rewriting those " +
            "would break the tables.",
            document => ReplaceIn(document, "description",
                "vulnerable -- if not dead", "vulnerable — if not dead"))
    ];

    /// <summary>Applies every adjudication that names this document.</summary>
    private static void Adjudicate(string contentType, string key, JsonObject document)
    {
        foreach (var adjudication in Adjudications
                     .Where(entry => entry.ContentType == contentType && entry.Key == key))
        {
            if (!adjudication.Apply(document))
            {
                throw new InvalidOperationException(
                    $"The adjudication for {contentType}/{key} changed nothing. The archive no " +
                    $"longer holds what it corrects, so it is stale and must be removed. " +
                    $"Recorded reason: {adjudication.Reason}");
            }
        }
    }

    private static bool ReplaceIn(JsonObject document, string field, string find, string replace)
    {
        if (LegacyArchive.Text(document, field) is not { } text || !text.Contains(find, StringComparison.Ordinal))
        {
            return false;
        }

        document[field] = text.Replace(find, replace, StringComparison.Ordinal);

        return true;
    }

    private static bool RemoveFeature(JsonObject document, int level, string printed)
    {
        var row = (LegacyArchive.Array(document, "progression") ?? [])
            .OfType<JsonObject>()
            .SingleOrDefault(candidate => LegacyArchive.Int(candidate, "level") == level);

        if (row is null || LegacyArchive.Array(row, "features") is not { } features)
        {
            return false;
        }

        var index = features
            .Select((value, position) => (value, position))
            .Where(entry => entry.value is JsonValue node &&
                            node.TryGetValue<string>(out var text) &&
                            text == printed)
            .Select(entry => (int?)entry.position)
            .FirstOrDefault();

        if (index is null)
        {
            return false;
        }

        features.RemoveAt(index.Value);

        if (features.Count == 0)
        {
            row.Remove("features");
        }

        return true;
    }

    // --------------------------------------------------------------- tidying

    /// <summary>
    /// Drops labelled cells whose value did not survive repair.
    /// </summary>
    /// <remarks>
    /// A class or archetype level table prints an em dash in every column that
    /// says nothing at that level, and the scrape turned each of those dashes
    /// into a lone replacement character. Repair correctly reads that as a
    /// total loss and removes the value, which would otherwise leave a cell
    /// that is a column heading and nothing else. Removing the cell entirely is
    /// what the printed table means: the row simply does not carry that column,
    /// and a renderer that fills a missing column with an em dash reproduces
    /// the page exactly.
    /// </remarks>
    private static void DropCellsEmptiedByRepair(JsonObject document)
    {
        foreach (var row in (LegacyArchive.Array(document, "progression") ?? []).OfType<JsonObject>())
        {
            if (LegacyArchive.Array(row, "entries") is not { } entries)
            {
                continue;
            }

            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (entries[index] is JsonObject cell && cell["value"] is null)
                {
                    entries.RemoveAt(index);
                }
            }

            if (entries.Count == 0)
            {
                row.Remove("entries");
            }
        }
    }

    /// <summary>The file one imported document is written to, relative to the repository root.</summary>
    public static string PathOf(ImportedDocument document) =>
        $"content/{document.ContentType}/{document.Key}.json";

    /// <summary>One document, formatted exactly as it is committed.</summary>
    public static string Serialize(JsonObject document) =>
        document.ToJsonString(FileFormat) + "\n";
}
