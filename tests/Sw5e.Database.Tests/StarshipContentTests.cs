using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Sw5e.Database.Tests;

/// <summary>
/// Holds the six starship content types to the corpus they were imported from.
/// </summary>
/// <remarks>
/// <para>
/// These types are deliberately not in <see cref="ArchiveConformanceTests"/>.
/// That suite asks whether a <em>mechanical</em> field rename of an archive
/// record satisfies a schema, and for starships the answer is "no, and the
/// schema is right". Three of the six starship files lost their structured
/// columns to the 2022 scrape and kept only prose: every numeric field on all
/// six <c>StarshipBaseSize</c> records is zero and every list is null, so the
/// hull dice, the modification budget, the roles and the tier table exist only
/// inside <c>fullText</c>; and all nineteen <c>StarshipEquipment</c> ammunition
/// records carry a name and a price and nothing else, with their damage,
/// weight, range and properties printed in the rules chapter instead. Loosening
/// the schemas until a flattened record fit them would publish a starship
/// section that cannot say how much hull a Small ship has.
/// </para>
/// <para>
/// So the contract asserted here is the stronger one that actually matters:
/// the canonical set covers the archive one-for-one, and every value the
/// archive genuinely carries survives into it unchanged. What the archive lost
/// is named in <see cref="ArchiveFieldsLostToTheScrape"/> and asserted to still
/// be lost, so that a re-scrape makes this list go stale loudly rather than
/// leaving a recovered field quietly unused.
/// </para>
/// </remarks>
public sealed class StarshipContentTests(ITestOutputHelper output)
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    /// <summary>
    /// Content type, the archive file it was imported from, and how many items
    /// that file holds. The counts are asserted on both sides: a canonical
    /// directory that has drifted from the archive is the failure this suite
    /// exists to catch, and "more than zero" would pass on a single file.
    /// </summary>
    private static readonly (string ContentType, string ArchiveFile, int ItemCount)[] Corpus =
    [
        ("starship-base-size", "StarshipBaseSize", 6),
        ("starship-deployment", "StarshipDeployment", 6),
        ("starship-equipment", "StarshipEquipment", 104),
        ("starship-modification", "StarshipModification", 257),
        ("starship-venture", "StarshipVenture", 67),
        ("starship-rule", "starshipRule", 13),
    ];

    /// <summary>
    /// Names the import corrected, and why. Every one is a transcription defect
    /// rather than a style choice: the six modifications and one venture were
    /// the only entries in their tables set in capitals, and one plating
    /// modification lost the space after its comma while its three siblings
    /// kept theirs. SLAM is deliberately absent — it is an acronym, and its own
    /// body text writes it in capitals too.
    /// </summary>
    private static readonly Dictionary<string, string> CorrectedNames = new(StringComparer.Ordinal)
    {
        ["AMPHIBIOUS SYSTEMS"] = "Amphibious Systems",
        ["HOLDING CELLS"] = "Holding Cells",
        ["MINING LASER"] = "Mining Laser",
        ["PILOTING CHAMBER"] = "Piloting Chamber",
        ["TRIBUTARY BEAM"] = "Tributary Beam",
        ["Plating,Reinforced  Mk II"] = "Plating, Reinforced Mk II",
        ["TARGETING FIRE"] = "Targeting Fire",
        ["Precision GUNNER"] = "Precision Gunner",
    };

    /// <summary>
    /// Archive fields whose values the scrape destroyed, and where the
    /// published value was recovered from instead. Each is asserted to still be
    /// empty in the archive, so that a future archive that carries real values
    /// fails here rather than being silently ignored by an import that no
    /// longer needs it.
    /// </summary>
    private static readonly (string ArchiveFile, string Field, string Recovery)[]
        ArchiveFieldsLostToTheScrape =
        [
            ("StarshipBaseSize", "hitDiceNumberOfDice",
                "Zero on all six sizes. Hull and shield dice are read from the fullText prose."),
            ("StarshipBaseSize", "modSlotsAtTier0",
                "Zero on all six sizes. The modification budget is read from the fullText prose."),
            ("StarshipBaseSize", "maxSuiteSystems",
                "Zero on all six sizes. The suite cap is read from the fullText prose."),
            ("StarshipEquipment", "armorClassBonus",
                "Zero on all 104 parts, armour included. Armor class comes from the " +
                "Armor and Shields table in rules chapter 5."),
            ("StarshipEquipment", "attacksPerRound",
                "Zero on all 104 parts. Rate of fire is expressed by the rapid and burst " +
                "properties instead, which did survive."),
            ("StarshipEquipment", "attackBonus",
                "Zero on all 104 parts. Ship weapons add the crew's bonus, not the weapon's."),
            ("StarshipEquipment", "damageDieModifier",
                "Zero on all 104 parts. No ship weapon in the book adds a flat modifier."),
        ];

    /* ------------------------------------------------------------ loading */

    private static IReadOnlyList<JsonObject> Documents(string contentType)
    {
        var directory = Path.Combine(ContentRoot, contentType);

        Directory.Exists(directory).ShouldBeTrue($"No content directory at '{directory}'.");

        return Directory
            .EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(file => JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidOperationException($"{file} is not a JSON object."))
            .ToList();
    }

    private static string Name(JsonObject document) =>
        LegacyArchive.Text(document, "name") ?? LegacyArchive.Text(document, "title")!;

    private static Dictionary<string, JsonObject> ByName(string contentType) =>
        Documents(contentType).ToDictionary(Name, document => document, StringComparer.Ordinal);

    private static string Corrected(string archiveName) =>
        CorrectedNames.TryGetValue(archiveName, out var corrected) ? corrected : archiveName;

    /// <summary>
    /// Runs an assertion only where the legacy archive is checked out. It is
    /// not part of this repository, so CI on a fresh clone has to skip rather
    /// than fail.
    /// </summary>
    private bool TryArchive(out string archive)
    {
        var located = LegacyArchive.TryLocate();

        if (located is null)
        {
            output.WriteLine(LegacyArchive.MissingArchiveMessage);
            archive = "";
            return false;
        }

        archive = located;
        return true;
    }

    /* -------------------------------------------------------------- shape */

    [Fact]
    public void EveryStarshipTypeCarriesItsWholeArchiveFile()
    {
        var totals = new List<string>();

        foreach (var (contentType, _, expected) in Corpus)
        {
            var documents = Documents(contentType);

            documents.Count.ShouldBe(expected,
                $"content/{contentType} holds {documents.Count} documents; the archive file it " +
                $"was imported from holds {expected}. Starships are published whole or not at all.");

            documents.Select(Name).Distinct(StringComparer.Ordinal).Count().ShouldBe(expected,
                $"content/{contentType} has two documents with the same display name, which " +
                "would collide on the site, where the URL is derived from the name.");

            totals.Add($"{contentType}: {documents.Count}");
        }

        output.WriteLine(string.Join(Environment.NewLine, totals));

        Corpus.Sum(entry => entry.ItemCount).ShouldBe(453);
    }

    [Fact]
    public void EveryArchiveRecordHasExactlyOneCanonicalDocument()
    {
        if (!TryArchive(out var archive))
        {
            return;
        }

        var failures = new List<string>();

        foreach (var (contentType, archiveFile, expected) in Corpus)
        {
            var records = LegacyArchive.Read(archive, archiveFile);
            records.Count.ShouldBe(expected, $"{archiveFile}.json has changed size.");

            var published = ByName(contentType);

            foreach (var record in records)
            {
                // Rule chapters are the one starship file with no name field:
                // they are titled by chapter.
                var archiveName = LegacyArchive.Text(record, "name")
                    ?? LegacyArchive.Text(record, "chapterName")!;
                var expectedName = Corrected(archiveName);

                if (!published.ContainsKey(expectedName))
                {
                    failures.Add($"{contentType}: the archive has '{archiveName}' " +
                                 $"and the content set has no '{expectedName}'.");
                }
            }

            foreach (var name in published.Keys)
            {
                var known = records.Any(record =>
                    Corrected(LegacyArchive.Text(record, "name")
                              ?? LegacyArchive.Text(record, "chapterName")!) == name);

                if (!known)
                {
                    failures.Add($"{contentType}: '{name}' is published but is in no archive record.");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    /* ------------------------------------------------- values carried over */

    [Fact]
    public void EveryModificationKeepsItsArchivedTypeGradeAndPrerequisiteWording()
    {
        if (!TryArchive(out var archive))
        {
            return;
        }

        var published = ByName("starship-modification");
        var failures = new List<string>();

        foreach (var record in LegacyArchive.Read(archive, "StarshipModification"))
        {
            var name = Corrected(LegacyArchive.Text(record, "name")!);
            var document = published[name];

            var expectedType = LegacyArchive.CamelCase(LegacyArchive.Text(record, "type")!);
            var actualType = LegacyArchive.Text(document, "modificationType");

            if (actualType != expectedType)
            {
                failures.Add($"{name}: type is '{actualType}', the archive says '{expectedType}'.");
            }

            var expectedGrade = LegacyArchive.Int(record, "grade");
            var actualGrade = LegacyArchive.Int(document, "grade");

            if (actualGrade != expectedGrade)
            {
                failures.Add($"{name}: grade is {actualGrade}, the archive says {expectedGrade}.");
            }

            // Every printed clause has to survive somewhere in the structured
            // list, because `text` is what a page shows a reader. Splitting on
            // the semicolon is the only division the import makes.
            var clauses = LegacyArchive.Strings(record, "prerequisites")
                .SelectMany(value => value.Split(';'))
                .Select(clause => clause.Trim())
                .Where(clause => clause.Length > 0)
                .ToList();

            var texts = (document["prerequisites"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(entry => LegacyArchive.Text(entry, "text")!)
                .ToList();

            foreach (var clause in clauses)
            {
                if (!texts.Any(text => clause.Contains(text, StringComparison.Ordinal)))
                {
                    failures.Add($"{name}: the archived prerequisite '{clause}' is not " +
                                 "printed by any entry of the published list.");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryWeaponKeepsItsArchivedDamageRangeAndCost()
    {
        if (!TryArchive(out var archive))
        {
            return;
        }

        var published = ByName("starship-equipment");
        var failures = new List<string>();
        var compared = 0;

        foreach (var record in LegacyArchive.Read(archive, "StarshipEquipment"))
        {
            var name = LegacyArchive.Text(record, "name")!;
            var document = published[name];

            if (LegacyArchive.Int(document, "costInCredits") != LegacyArchive.Int(record, "cost"))
            {
                failures.Add($"{name}: cost does not match the archive.");
            }

            if (LegacyArchive.Text(record, "type") != "Weapon")
            {
                continue;
            }

            compared++;

            var damage = LegacyArchive.Object(document, "damage");
            var dice = LegacyArchive.Int(record, "damageNumberOfDice") ?? 0;

            if (dice == 0)
            {
                // The ten launchers deal no damage of their own.
                if (damage is not null)
                {
                    failures.Add($"{name}: publishes damage the archive does not have.");
                }
            }
            else if (LegacyArchive.Int(damage!, "numberOfDice") != dice ||
                     LegacyArchive.Int(damage!, "dieFaces") != LegacyArchive.Int(record, "damageDieType"))
            {
                failures.Add($"{name}: damage dice do not match the archive.");
            }

            var range = LegacyArchive.Object(document, "range");
            var shortRange = LegacyArchive.Int(record, "shortRange") ?? 0;

            if (shortRange > 0 &&
                (LegacyArchive.Int(range!, "normal") != shortRange ||
                 LegacyArchive.Int(range!, "long") != LegacyArchive.Int(record, "longRange")))
            {
                failures.Add($"{name}: range does not match the archive.");
            }
        }

        compared.ShouldBe(62, "the archive holds 62 ship weapons; all of them must be compared.");
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryRuleChapterKeepsItsNumberAndItsWholeBody()
    {
        if (!TryArchive(out var archive))
        {
            return;
        }

        var published = ByName("starship-rule");
        var failures = new List<string>();

        foreach (var record in LegacyArchive.Read(archive, "starshipRule"))
        {
            var title = LegacyArchive.Text(record, "chapterName")!;
            var document = published[title];

            if (LegacyArchive.Int(document, "chapterNumber") != LegacyArchive.Int(record, "chapterNumber"))
            {
                failures.Add($"{title}: chapter number does not match the archive.");
            }

            var archived = LegacyArchive.Text(record, "contentMarkdown")!;
            var body = LegacyArchive.Text(document, "body")!;

            // Line endings are normalised and one cell was repaired, so the
            // bodies are not byte-identical. A chapter that lost a tenth of its
            // length lost a section, and that is what this catches.
            if (body.Length < archived.Length * 0.9)
            {
                failures.Add($"{title}: the published body is {body.Length} characters, " +
                             $"the archived chapter is {archived.Length}.");
            }
        }

        published.Count.ShouldBe(13);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TheFieldsTheScrapeDestroyedAreStillEmptyInTheArchive()
    {
        if (!TryArchive(out var archive))
        {
            return;
        }

        var stale = new List<string>();

        foreach (var (archiveFile, field, recovery) in ArchiveFieldsLostToTheScrape)
        {
            var records = LegacyArchive.Read(archive, archiveFile);

            var carriesValue = records.Any(record =>
                LegacyArchive.Int(record, field) is { } value && value != 0);

            if (carriesValue)
            {
                stale.Add($"{archiveFile}.{field} now carries a value. {recovery} " +
                          "Import it from the archive instead, and delete this entry.");
            }
        }

        stale.ShouldBeEmpty(string.Join(Environment.NewLine, stale));
    }

    /* ------------------------------------------------------- cross-links */

    [Fact]
    public void EveryStarshipCrossReferenceResolves()
    {
        var modifications = ByName("starship-modification").Keys.ToHashSet(StringComparer.Ordinal);
        var equipment = ByName("starship-equipment");
        var equipmentNames = equipment.Keys.ToHashSet(StringComparer.Ordinal);
        var ventures = ByName("starship-venture").Keys.ToHashSet(StringComparer.Ordinal);
        var deployments = ByName("starship-deployment").Keys.ToHashSet(StringComparer.Ordinal);

        var weaponNames = equipment.Values
            .Where(document => LegacyArchive.Text(document, "category") == "weapon")
            .Select(Name)
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();
        var resolved = 0;

        void Require(bool ok, string owner, string field, string value, string target)
        {
            if (ok)
            {
                resolved++;
                return;
            }

            failures.Add($"{owner}: {field} names {target} '{value}', which is not published.");
        }

        foreach (var document in Documents("starship-modification"))
        {
            foreach (var entry in (document["prerequisites"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (LegacyArchive.Text(entry, "modificationName") is { } required)
                {
                    Require(modifications.Contains(required), Name(document),
                        "prerequisites", required, "modification");
                }

                if (LegacyArchive.Text(entry, "equipmentName") is { } part)
                {
                    Require(equipmentNames.Contains(part), Name(document),
                        "prerequisites", part, "equipment");
                }
            }
        }

        foreach (var document in Documents("starship-venture"))
        {
            foreach (var entry in (document["prerequisites"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (LegacyArchive.Text(entry, "ventureName") is { } required)
                {
                    Require(ventures.Contains(required), Name(document),
                        "prerequisites", required, "venture");
                }

                if (LegacyArchive.Text(entry, "deploymentName") is { } station)
                {
                    Require(deployments.Contains(station), Name(document),
                        "prerequisites", station, "deployment");
                }
            }
        }

        foreach (var document in Documents("starship-equipment"))
        {
            foreach (var launcher in LegacyArchive.Strings(document, "firedBy"))
            {
                Require(weaponNames.Contains(launcher), Name(document),
                    "firedBy", launcher, "weapon");
            }
        }

        // A role names its systems by their leading word — "Deflection",
        // "Quick-Charge", "Hub & Spoke" — because that is how the size tables
        // print them. Resolving them is what lets a ship built from a role be
        // costed without a human reading the table.
        foreach (var document in Documents("starship-base-size"))
        {
            foreach (var role in (document["roles"] as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var field in new[] { "armor", "shields", "reactor", "powerCoupling" })
                {
                    if (LegacyArchive.Text(role, field) is not { } lead)
                    {
                        continue;
                    }

                    var match = equipmentNames.Any(part =>
                        part.StartsWith(lead, StringComparison.OrdinalIgnoreCase));

                    Require(match, $"{Name(document)} / {LegacyArchive.Text(role, "name")}",
                        field, lead, "equipment");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));

        // Guards the guard: an import that emitted no links at all would leave
        // every loop above with nothing to check and pass silently.
        resolved.ShouldBeGreaterThan(200,
            $"only {resolved} starship cross-references resolved; the corpus carries far more, " +
            "so this many means the links stopped being emitted.");
    }

    /// <summary>
    /// Rows where the printed tier table and the printed feature blocks
    /// disagree about a feature's name. All three are defects in the book
    /// rather than in the import — the table and the body were typeset from
    /// different drafts — and both spellings are published as printed. Pinning
    /// them here is what keeps a fourth from appearing unnoticed.
    /// </summary>
    private static readonly Dictionary<(string Size, string TableName), string> RenamedInTheBody =
        new()
        {
            [("Large", "Heavy Cannon")] =
                "The body writes this 3rd-tier feature as Super-Heavy Turbolaser Battery.",
            [("Tiny", "Evasion")] =
                "The body has no Evasion block; its 3rd-tier defensive feature is written " +
                "as Uncanny Dodge, which the table in turn lists at 2nd tier.",
            [("Tiny", "Role Mastery")] =
                "The body writes the 4th-tier feature as a second Role Specialization block.",
        };

    [Fact]
    public void EveryTierAndRankTableNamesFeaturesThatAreWrittenOut()
    {
        var failures = new List<string>();
        var known = new HashSet<(string, string)>(RenamedInTheBody.Keys);

        foreach (var document in Documents("starship-base-size"))
        {
            var written = (document["features"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(feature => LegacyArchive.Text(feature, "name")!)
                .ToHashSet(StringComparer.Ordinal);

            var tiers = LegacyArchive.Object(document, "tierProgression")!["tiers"] as JsonArray ?? [];

            tiers.Count.ShouldBe(6, $"{Name(document)}: a size runs from tier 0 to tier 5.");

            foreach (var tier in tiers.OfType<JsonObject>())
            {
                foreach (var feature in LegacyArchive.Strings(tier, "features"))
                {
                    // Role and the ability score increase are granted by the
                    // size itself rather than by a feature block of their own.
                    if (feature is "Role" or "Ability Score Increase" ||
                        written.Contains(feature))
                    {
                        continue;
                    }

                    if (known.Remove((Name(document), feature)))
                    {
                        continue;
                    }

                    failures.Add($"{Name(document)}: tier " +
                                 $"{LegacyArchive.Int(tier, "tier")} grants '{feature}', " +
                                 "which no feature of that size writes out.");
                }
            }
        }

        foreach (var document in Documents("starship-deployment"))
        {
            var ranks = document["rankProgression"] as JsonArray ?? [];

            ranks.Count.ShouldBe(5, $"{Name(document)}: a deployment runs from rank 1 to rank 5.");

            foreach (var rank in ranks.OfType<JsonObject>())
            {
                LegacyArchive.Strings(rank, "features").ShouldNotBeEmpty(
                    $"{Name(document)}: rank {LegacyArchive.Int(rank, "rank")} grants nothing.");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));

        // Every pinned discrepancy must still be one. If the book is ever
        // corrected upstream, this is what says so rather than leaving a stale
        // exemption in place for good.
        known.ShouldBeEmpty(
            "these tier-table rows now match the body and no longer need pinning: " +
            string.Join(", ", known.Select(entry => $"{entry.Item1} / {entry.Item2}")));
    }

    /* ------------------------------------------------ recovered from prose */

    /// <summary>
    /// The values that exist only because the import read them out of prose.
    /// Each of these is zero, null or a bare hyphen in the archive record it
    /// belongs to, so a regression in the import shows up here as a wrong
    /// number rather than as a missing field a looser assertion would tolerate.
    /// </summary>
    [Fact]
    public void ValuesRecoveredFromProseAreTheOnesTheBookPrints()
    {
        var sizes = ByName("starship-base-size");

        var gargantuan = sizes["Gargantuan"];
        LegacyArchive.Int(LegacyArchive.Object(gargantuan, "modifications")!,
            "baseModificationSlots").ShouldBe(70);
        LegacyArchive.Int(
            LegacyArchive.Object(LegacyArchive.Object(gargantuan, "hull")!, "diceAtTier0")!,
            "number").ShouldBe(11);
        LegacyArchive.Int(
            LegacyArchive.Object(LegacyArchive.Object(gargantuan, "hull")!, "diceAtTier0")!,
            "faces").ShouldBe(20);

        var tiny = sizes["Tiny"];
        LegacyArchive.Text(LegacyArchive.Object(tiny, "tierProgression")!, "dieName")
            .ShouldBe("Swarm Tactics Die");
        LegacyArchive.Int(LegacyArchive.Object(tiny, "modifications")!,
            "baseModificationSlots").ShouldBe(10);
        // Tiny ships are unmanned, so the suite cap is printed as a dash.
        LegacyArchive.Object(tiny, "modifications")!
            .ContainsKey("maximumSuiteSystems").ShouldBeFalse();

        var medium = sizes["Medium"];
        // Medium is the baseline every other size is stated relative to.
        medium.ContainsKey("abilityScoreAdjustments").ShouldBeFalse();

        foreach (var size in sizes.Values)
        {
            (size["roles"] as JsonArray)!.Count.ShouldBe(6,
                $"{Name(size)}: every size offers exactly six roles.");
        }

        var equipment = ByName("starship-equipment");

        var fortress = LegacyArchive.Object(equipment["Fortress shield"], "shield")!;
        LegacyArchive.Text(fortress, "capacityMultiplier").ShouldBe("x 3/2");
        LegacyArchive.Text(fortress, "regenerationRateCoefficient").ShouldBe("x 2/3");

        var reinforced = LegacyArchive.Object(equipment["Reinforced armor"], "armor")!;
        LegacyArchive.Text(reinforced, "armorClass").ShouldBe("10 + Dex modifier (max 0)");
        LegacyArchive.Int(reinforced, "damageReduction").ShouldBe(6);
        LegacyArchive.Bool(reinforced, "stealthDisadvantage").ShouldBe(true);

        var missile = equipment["Adv. cluster missile"];
        var damage = LegacyArchive.Object(missile, "damage")!;
        LegacyArchive.Int(damage, "numberOfDice").ShouldBe(3);
        LegacyArchive.Int(damage, "dieFaces").ShouldBe(6);
        LegacyArchive.Text(damage, "type").ShouldBe("kinetic");
        LegacyArchive.Int(LegacyArchive.Object(missile, "damageForLargerShips")!,
            "numberOfDice").ShouldBe(6);
        LegacyArchive.Strings(missile, "firedBy").ShouldContain("Cluster pod launcher");
    }

    [Fact]
    public void EveryPieceOfAmmunitionKnowsWhatFiresItAndWhatItDoes()
    {
        var ammunition = Documents("starship-equipment")
            .Where(document => LegacyArchive.Text(document, "category") == "ammunition")
            .ToList();

        ammunition.Count.ShouldBe(19,
            "the archive holds nineteen pieces of ammunition, one for every row of the " +
            "Tertiary Ammunition table.");

        var failures = new List<string>();

        foreach (var round in ammunition)
        {
            if (LegacyArchive.Strings(round, "firedBy").Any())
            {
                continue;
            }

            failures.Add($"{Name(round)}: no launcher fires it, so it cannot be bought for a ship.");
        }

        // Six rounds are a special rule rather than a damage roll — a conner
        // net, a discord missile, an s-thread tracer and their kin — so this is
        // a floor rather than a total.
        ammunition.Count(round => round.ContainsKey("damage")).ShouldBe(15);
        ammunition.Count(round => round.ContainsKey("weightInPounds")).ShouldBe(19);

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryVentureThatNamesAPrerequisiteResolvesOrSaysWhyNot()
    {
        var ventures = Documents("starship-venture");

        var withPrerequisites = ventures
            .Count(document => document["prerequisites"] is JsonArray { Count: > 0 });

        withPrerequisites.ShouldBe(53,
            "fifty-three of the sixty-seven ventures print a prerequisite.");

        var unresolved = ventures
            .SelectMany(document => (document["prerequisites"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(entry => LegacyArchive.Text(entry, "kind") == "other")
            .Select(entry => LegacyArchive.Text(entry, "text")!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Both name a venture the book never printed. They are left
        // unstructured rather than guessed at, and pinned here so that a
        // third one cannot appear unnoticed.
        unresolved.ShouldBe(["Dual Roles", "Multi-Roles"]);
    }
}
