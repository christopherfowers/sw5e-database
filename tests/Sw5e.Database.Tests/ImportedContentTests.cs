using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Sw5e.Database.Tests;

/// <summary>
/// Holds the committed class graph — classes, class improvements, archetypes
/// and the features they grant — to the archive it was imported from.
/// </summary>
/// <remarks>
/// <para>
/// Two thousand nine hundred files cannot be reviewed by reading them. What can
/// be reviewed is the code that produced them, and that is only worth reviewing
/// if the files are provably its output. So this asserts exactly that: for
/// every document in the four directories this stream owns, the bytes on disk
/// are what <see cref="LegacyContentImport"/> produces from the archive today.
/// A hand-edit to one of those files fails here until it is expressed as a rule
/// or as a named adjudication, and a change to the import shows up as a diff
/// somebody can read.
/// </para>
/// <para>
/// Regenerate with <c>SW5E_WRITE_CONTENT=1 dotnet test --filter
/// ImportedContentTests</c>, which rewrites the four directories and then
/// asserts as normal. That is the only supported way to change these files.
/// </para>
/// <para>
/// Everything except the byte comparison runs from the archive, so on a machine
/// without it the suite reports why it skipped rather than passing silently —
/// the same contract <see cref="ArchiveConformanceTests"/> works to.
/// </para>
/// </remarks>
public sealed class ImportedContentTests(ITestOutputHelper output)
{
    private const string WriteVariable = "SW5E_WRITE_CONTENT";

    /// <summary>
    /// What the import is expected to produce, per content type.
    /// </summary>
    /// <remarks>
    /// These are the corpus's real counts, asserted so that an import which
    /// quietly drops half the archive fails instead of passing against a
    /// smaller set. Ten classes and one improvement of each of three kinds for
    /// each of them; a hundred and thirty-seven archetypes; and 2,682 features,
    /// which is the archive's 2,723 rows less the 41 duplicates the scrape left
    /// behind — 218 granted by a class, 871 by an archetype, 1,593 by a
    /// species.
    /// </remarks>
    private static readonly (string ContentType, int Count)[] Expected =
    [
        ("class", 10),
        ("class-improvement", 30),
        ("archetype", 137),
        ("feature", 2682)
    ];

    private static string ContentDirectory(string contentType) =>
        Path.Combine(LegacyArchive.RepositoryRoot, "content", contentType);

    private static IReadOnlyList<ImportedDocument>? Import(ITestOutputHelper output)
    {
        var archive = LegacyArchive.TryLocate();

        if (archive is null)
        {
            output.WriteLine(LegacyArchive.MissingArchiveMessage);
            return null;
        }

        return LegacyContentImport.Run(archive);
    }

    [Fact]
    public void TheImportProducesTheWholeClassGraph()
    {
        if (Import(output) is not { } documents)
        {
            return;
        }

        foreach (var (contentType, count) in Expected)
        {
            documents.Count(document => document.ContentType == contentType)
                .ShouldBe(count, $"the import produced the wrong number of {contentType} documents.");
        }

        documents
            .Select(document => $"{document.ContentType}/{document.Key}")
            .ShouldBeUnique();

        output.WriteLine($"{documents.Count} documents imported across {Expected.Length} content types.");
    }

    /// <summary>
    /// Spot checks with the book open. A count alone would pass on 1,089
    /// well-formed documents that said the wrong things, so these assert the
    /// facts a player would notice were wrong: what the berserker's table says
    /// at 1st level, how fast each caster advances, and that an archetype's
    /// features are reachable from the archetype.
    /// </summary>
    [Fact]
    public void ImportedClassesCarryTheirPublishedProgression()
    {
        if (Import(output) is not { } documents)
        {
            return;
        }

        var classes = ByKey(documents, "class");

        classes.Keys.Order(StringComparer.Ordinal).ShouldBe(
        [
            "berserker", "consular", "engineer", "fighter", "guardian",
            "monk", "operative", "scholar", "scout", "sentinel"
        ]);

        var berserker = classes["berserker"];
        var progression = berserker["progression"]!.AsArray();

        progression.Count.ShouldBe(20);
        progression.Select(row => (int)row!["level"]!).ShouldBe(Enumerable.Range(1, 20));

        berserker["hitPoints"]!["dieFaces"]!.GetValue<int>().ShouldBe(12);
        berserker["primaryAbility"]!.GetValue<string>().ShouldBe("strength");
        berserker["casterType"]!.GetValue<string>().ShouldBe("none");

        var first = progression[0]!.AsObject();
        first["proficiencyBonus"]!.GetValue<int>().ShouldBe(2);
        Strings(first, "features").ShouldBe(["Rage", "Unarmored Defense"]);

        // The 1st-level row prints an em dash under Berserker Instincts, so
        // that cell is absent while Rages and Rage Damage are present.
        var firstLevelCells = Cells(first);
        firstLevelCells["Rages"].ShouldBe("2");
        firstLevelCells["Rage Damage"].ShouldBe("+2");
        firstLevelCells.ShouldNotContainKey("Berserker Instincts");

        Cells(progression[19]!.AsObject())["Rage Damage"].ShouldBe("+5");

        // Proficiency bonus is the one column shared by every class, so a
        // mis-parse of it would be invisible on any single class.
        foreach (var (key, document) in classes)
        {
            foreach (var row in document["progression"]!.AsArray())
            {
                var level = row!["level"]!.GetValue<int>();

                row["proficiencyBonus"]!.GetValue<int>()
                    .ShouldBe(2 + (level - 1) / 4, $"{key} has the wrong proficiency bonus at level {level}.");
            }
        }

        // Casting advances at four different rates across the ten classes, and
        // the ratio is what a multiclass character's power points are computed
        // from, so getting one wrong is a rules bug rather than a display bug.
        classes["consular"]["casterRatio"]!.GetValue<double>().ShouldBe(1.0);
        classes["sentinel"]["casterRatio"]!.GetValue<double>().ShouldBe(2.0 / 3.0, 1e-9);
        classes["guardian"]["casterRatio"]!.GetValue<double>().ShouldBe(0.5);
        classes["fighter"]["casterRatio"]!.GetValue<double>().ShouldBe(0.0);
        classes["engineer"]["casterType"]!.GetValue<string>().ShouldBe("tech");
        classes["consular"]["casterType"]!.GetValue<string>().ShouldBe("force");

        // The operative is the only class that chooses from every skill, which
        // is recorded as an absent list rather than as a skill called "Any".
        var operativeSkills = classes["operative"]["proficiencies"]!["skills"]!.AsObject();
        operativeSkills["choose"]!.GetValue<int>().ShouldBe(4);
        operativeSkills.ShouldNotContainKey("from");

        Strings(classes["berserker"]["proficiencies"]!.AsObject(), "savingThrows")
            .ShouldBe(["strength", "constitution"]);
    }

    [Fact]
    public void ImportedArchetypesAndFeaturesFormOneGraph()
    {
        if (Import(output) is not { } documents)
        {
            return;
        }

        var classNames = ByKey(documents, "class")
            .Values
            .Select(document => LegacyArchive.Text(document, "name")!)
            .ToHashSet(StringComparer.Ordinal);

        var archetypes = ByKey(documents, "archetype");
        var archetypeNames = archetypes.Values
            .Select(document => LegacyArchive.Text(document, "name")!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (key, archetype) in archetypes)
        {
            classNames.ShouldContain(LegacyArchive.Text(archetype, "className")!,
                $"archetype '{key}' belongs to a class that was not imported.");
        }

        foreach (var (key, improvement) in ByKey(documents, "class-improvement"))
        {
            classNames.ShouldContain(LegacyArchive.Text(improvement, "className")!,
                $"class improvement '{key}' belongs to a class that was not imported.");
        }

        // Each class has exactly one of each kind of improvement, which is what
        // makes the three a set rather than three unrelated catalogues.
        ByKey(documents, "class-improvement").Values
            .GroupBy(document => LegacyArchive.Text(document, "improvementType")!)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .ShouldBe([("class", 10), ("multiclass", 10), ("splashclass", 10)]);

        var features = ByKey(documents, "feature").Values.ToList();

        var speciesNames = PublishedNames("species");

        foreach (var feature in features)
        {
            var grantedByName = LegacyArchive.Text(feature, "grantedByName")!;

            switch (LegacyArchive.Text(feature, "grantedBy"))
            {
                case "class":
                    classNames.ShouldContain(grantedByName);

                    // Level is what makes a feature reachable from a level
                    // table. A class or archetype feature without one could not
                    // be granted at all.
                    LegacyArchive.Int(feature, "level").ShouldNotBeNull(
                        $"'{LegacyArchive.Text(feature, "key")}' has no level.");
                    break;

                case "archetype":
                    archetypeNames.ShouldContain(grantedByName);
                    LegacyArchive.Int(feature, "level").ShouldNotBeNull(
                        $"'{LegacyArchive.Text(feature, "key")}' has no level.");
                    break;

                case "species":
                    // Species are somebody else's import; these features are
                    // ours, and they are only publishable because all 141 of
                    // them are now in the content set. A species feature is
                    // held from character creation, so it carries no level.
                    speciesNames.ShouldContain(grantedByName,
                        $"'{LegacyArchive.Text(feature, "key")}' names a species that is not published.");
                    LegacyArchive.Int(feature, "level").ShouldBeNull(
                        $"'{LegacyArchive.Text(feature, "key")}' is granted at a level, which no " +
                        "species feature is.");
                    break;

                default:
                    throw new InvalidOperationException(
                        $"'{LegacyArchive.Text(feature, "key")}' is granted by something this " +
                        "import does not own.");
            }
        }

        features.Count(feature => LegacyArchive.Text(feature, "grantedBy") == "class").ShouldBe(218);
        features.Count(feature => LegacyArchive.Text(feature, "grantedBy") == "archetype").ShouldBe(871);
        features.Count(feature => LegacyArchive.Text(feature, "grantedBy") == "species").ShouldBe(1593);

        // A feature is printed inside whatever grants it, so its provenance is
        // that entry's. The archive supplies none of its own, and the site
        // refuses to publish an item it cannot attribute to a book, so a
        // feature that lost this would simply not appear.
        var provenance = ByKey(documents, "class").Values
            .Concat(archetypes.Values)
            .Concat(Published("species"))
            .ToDictionary(
                document => LegacyArchive.Text(document, "name")!,
                document => (LegacyArchive.Text(document, "sourceKey"),
                             LegacyArchive.Text(document, "contentSet")),
                StringComparer.Ordinal);

        foreach (var feature in features)
        {
            var expected = provenance[LegacyArchive.Text(feature, "grantedByName")!];

            (LegacyArchive.Text(feature, "sourceKey"), LegacyArchive.Text(feature, "contentSet"))
                .ShouldBe(expected, LegacyArchive.Text(feature, "key"));
        }

        // Ataru Form is expanded content, so everything it grants is too — the
        // archive's storage partition files two of its features under Core,
        // and it is the partition that is wrong.
        ByKey(documents, "feature")["archetype-ataru-form-hawk-bat-swoop-7"]["sourceKey"]!
            .GetValue<string>().ShouldBe("ec");

        // Every class grants at least one feature at 1st level, or a character
        // could take it and gain nothing.
        foreach (var className in classNames)
        {
            features.ShouldContain(
                feature => LegacyArchive.Text(feature, "grantedByName") == className &&
                           LegacyArchive.Int(feature, "level") == 1,
                $"{className} grants nothing at 1st level.");
        }

        var rage = ByKey(documents, "feature")["class-berserker-rage-1"];
        rage["name"]!.GetValue<string>().ShouldBe("Rage");
        rage["level"]!.GetValue<int>().ShouldBe(1);
        rage["description"]!.GetValue<string>().ShouldContain("you can enter a rage as a bonus action");

        // Soresu Form is a guardian archetype whose 3rd-level Form Basics is
        // both printed in the archetype's own page and stored as its own
        // feature. Both halves have to be there for a detail page to show the
        // page and link the grant.
        var soresu = archetypes["soresu-form"];
        soresu["className"]!.GetValue<string>().ShouldBe("Guardian");
        soresu["description"]!.GetValue<string>().ShouldContain("Form Basics");
        ByKey(documents, "feature")["archetype-soresu-form-form-basics-3"]["name"]!
            .GetValue<string>().ShouldBe("Form Basics");
    }

    [Fact]
    public void NothingImportedStillCarriesLostCharacters()
    {
        if (Import(output) is not { } documents)
        {
            return;
        }

        var failures = documents
            .Where(document => LegacyTextRepair.ContainsUnrepairedLoss(
                document.Document.ToJsonString(LegacyContentImport.FileFormat)))
            .Select(document => LegacyContentImport.PathOf(document))
            .ToList();

        failures.ShouldBeEmpty(
            "These documents still hold U+FFFD after import, so they cannot be committed. " +
            "Either a repair rule needs extending or the case needs an adjudication:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The one that makes the other four worth something: what is on disk is
    /// what the import produces, byte for byte.
    /// </summary>
    [Fact]
    public void CommittedContentIsExactlyWhatTheImportProduces()
    {
        if (Import(output) is not { } documents)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable(WriteVariable) is "1")
        {
            Write(documents);
        }

        var failures = new List<string>();
        var expected = documents.ToDictionary(
            LegacyContentImport.PathOf, document => LegacyContentImport.Serialize(document.Document));

        foreach (var (path, serialized) in expected)
        {
            var file = Path.Combine(LegacyArchive.RepositoryRoot, path);

            if (!File.Exists(file))
            {
                failures.Add($"{path}: missing.");
                continue;
            }

            // Read as text so a checkout that translated line endings compares
            // equal: git owns that, the import does not.
            var actual = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n");

            if (actual != serialized)
            {
                failures.Add($"{path}: differs from what the import produces.");
            }
        }

        foreach (var contentType in LegacyContentImport.ContentTypes)
        {
            foreach (var file in Directory.Exists(ContentDirectory(contentType))
                         ? Directory.EnumerateFiles(ContentDirectory(contentType), "*.json")
                         : [])
            {
                var path = $"content/{contentType}/{Path.GetFileName(file)}";

                if (!expected.ContainsKey(path))
                {
                    failures.Add($"{path}: not produced by the import.");
                }
            }
        }

        failures.ShouldBeEmpty(
            $"content/ and the archive disagree. Regenerate with {WriteVariable}=1 and review the diff:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures.Take(20)) +
            (failures.Count > 20 ? $"{Environment.NewLine}  ... and {failures.Count - 20} more." : ""));
    }

    /// <summary>
    /// The published documents of a content type this import does not own,
    /// read off disk. Only species, and only so that a feature granted by one
    /// can be checked against the species that is actually published rather
    /// than against a second reading of the archive.
    /// </summary>
    private static IReadOnlyList<JsonObject> Published(string contentType)
    {
        var directory = ContentDirectory(contentType);

        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.json")
                .Order(StringComparer.Ordinal)
                .Select(file => JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject)
                .Where(document => document is not null)
                .Select(document => document!)]
            : [];
    }

    private static HashSet<string> PublishedNames(string contentType) =>
        [.. Published(contentType).Select(document => LegacyArchive.Text(document, "name")!)];

    private static void Write(IReadOnlyList<ImportedDocument> documents)
    {
        foreach (var contentType in LegacyContentImport.ContentTypes)
        {
            var directory = ContentDirectory(contentType);
            Directory.CreateDirectory(directory);

            var keep = documents
                .Where(document => document.ContentType == contentType)
                .Select(document => Path.GetFileName(LegacyContentImport.PathOf(document)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }

        foreach (var document in documents)
        {
            File.WriteAllText(
                Path.Combine(LegacyArchive.RepositoryRoot, LegacyContentImport.PathOf(document)),
                LegacyContentImport.Serialize(document.Document),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static Dictionary<string, JsonObject> ByKey(
        IReadOnlyList<ImportedDocument> documents, string contentType) =>
        documents
            .Where(document => document.ContentType == contentType)
            .ToDictionary(document => document.Key, document => document.Document, StringComparer.Ordinal);

    private static IReadOnlyList<string> Strings(JsonObject document, string field) =>
        [.. (LegacyArchive.Array(document, field) ?? [])
            .Select(value => value!.GetValue<string>())];

    /// <summary>One progression row's labelled cells, as a lookup by heading.</summary>
    private static Dictionary<string, string> Cells(JsonObject row) =>
        (LegacyArchive.Array(row, "entries") ?? [])
            .OfType<JsonObject>()
            .ToDictionary(
                cell => cell["label"]!.GetValue<string>(),
                cell => cell["value"]!.GetValue<string>(),
                StringComparer.Ordinal);
}
