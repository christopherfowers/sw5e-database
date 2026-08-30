using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Sw5e.Database.Tests.LegacyArchive;

namespace Sw5e.Database.Tests;

/// <summary>
/// Mechanically rewrites a legacy archive record into the shape the v1 content
/// schemas define. This is the executable statement of the field mapping the
/// importer will implement, so it deliberately does no repair: it renames,
/// regroups, and drops storage artefacts, and it passes corrupt values through
/// untouched.
/// <para>
/// Rules applied to every content type:
/// every field whose name ends in <c>Json</c> is a stringified duplicate and is
/// dropped; <c>partitionKey</c>, <c>rowKey</c>, <c>timestamp</c> and <c>eTag</c>
/// are storage bookkeeping and are dropped; each <c>*Enum</c> field is dropped in
/// favour of its human-readable sibling; <c>contentSource</c> becomes a
/// lower-case <c>sourceKey</c>; <c>contentType</c> becomes <c>contentSet</c>; and
/// the finished object is pruned of nulls, blank strings and empty containers.
/// </para>
/// </summary>
public static class LegacyContentMapper
{
    public static JsonObject Map(string contentType, JsonObject item) => contentType switch
    {
        "species" => Finish(Species(item)),
        "background" => Finish(Background(item)),
        "feat" => Finish(Feat(item)),
        "power" => Finish(Power(item)),
        "equipment" => Finish(Equipment(item)),
        "monster" => Finish(Monster(item)),
        "archetype" => Finish(Archetype(item)),
        "feature" => Finish(Feature(item)),
        "maneuver" => Finish(Maneuver(item)),
        "fighting-style" => Finish(Bulleted(item, "description")),
        "fighting-mastery" => Finish(Bulleted(item, "text")),
        "lightsaber-form" => Finish(LightsaberForm(item)),
        "weapon-focus" => Finish(WeaponGrouped(item, " Focus")),
        "weapon-supremacy" => Finish(WeaponGrouped(item, " Supremacy")),
        "class" => Finish(Class(item)),

        // The three improvement files hold identical records and become one
        // content type, so the kind cannot be read off the record: nothing in
        // it says which of the three files it came from. The caller names the
        // kind after the slash, and the part before the slash is the content
        // type, and therefore the schema, the result is validated against.
        "class-improvement/class" => Finish(ClassImprovement(item, "class")),
        "class-improvement/multiclass" => Finish(ClassImprovement(item, "multiclass")),
        "class-improvement/splashclass" => Finish(ClassImprovement(item, "splashclass")),

        "enhanced-item" => Finish(EnhancedItem(item)),
        "weapon-property" or "armor-property" => Finish(Property(item)),
        "reference-table" => Finish(ReferenceTable(item)),

        // And four more mapping keys that are not content types. Every rules
        // record in the archive has a contentSource of "None", so nothing in
        // one says which book printed it; the file it came from is the only
        // evidence there is, and the caller names it after the slash.
        "rule/phb" => Finish(Rule(item, "phb", "core", "chapter")),
        "rule/wh" => Finish(Rule(item, "wh", "core", "chapter")),
        "rule/ec" => Finish(Rule(item, "ec", "expanded-content", "chapter")),
        "rule/variant" => Finish(Rule(item, "ec", "expanded-content", "variant")),

        _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "No mapping defined.")
    };

    /// <summary>
    /// The content type, and so the schema directory, a mapping key belongs to.
    /// Everything before the first slash; keys without one are already the
    /// content type.
    /// </summary>
    public static string SchemaType(string mappingKey) =>
        mappingKey.Split('/', 2)[0];

    private static JsonObject Finish(JsonObject mapped) =>
        Prune(mapped) as JsonObject ?? new JsonObject();

    private static JsonObject Provenance(JsonObject item)
    {
        var source = Text(item, "contentSource");
        var set = Text(item, "contentType");

        return new JsonObject
        {
            ["sourceKey"] = source is null ? null : Lower(source),
            ["contentSet"] = set switch
            {
                "Core" => "core",
                "ExpandedContent" => "expanded-content",
                null => null,
                _ => set
            }
        };
    }

    private static JsonObject With(JsonObject target, JsonObject additions)
    {
        foreach (var property in additions.ToList())
        {
            target[property.Key] = property.Value?.DeepClone();
        }

        return target;
    }

    // ---------------------------------------------------------------- species

    private static JsonObject Species(JsonObject item)
    {
        var name = Text(item, "name")!;
        var size = Text(item, "size");

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["size"] = size is null ? null : Lower(size),
            ["homeworld"] = Text(item, "homeworld"),
            ["nativeLanguage"] = Text(item, "language"),
            ["lore"] = Text(item, "flavorText"),
            ["traits"] = new JsonArray(Array(item, "traits")!
                .OfType<JsonObject>()
                .Select(trait => (JsonNode)new JsonObject
                {
                    ["name"] = Text(trait, "name"),
                    ["description"] = Text(trait, "description")
                })
                .ToArray()),
            ["abilityScoreIncreaseOptions"] = new JsonArray(Array(item, "abilitiesIncreased")!
                .OfType<JsonArray>()
                .Select(option => (JsonNode)new JsonObject
                {
                    ["increases"] = new JsonArray(option
                        .OfType<JsonObject>()
                        .Select(AbilityIncrease)
                        .ToArray())
                })
                .ToArray()),
            ["physique"] = new JsonObject
            {
                ["heightAverage"] = Text(item, "heightAverage"),
                ["heightModifier"] = Text(item, "heightRollMod"),
                ["weightAverage"] = Text(item, "weightAverage"),
                ["weightModifier"] = Text(item, "weightRollMod")
            },
            ["appearance"] = new JsonObject
            {
                ["distinctions"] = Text(item, "distinctions"),
                ["skinColorOptions"] = Text(item, "skinColorOptions"),
                ["hairColorOptions"] = Text(item, "hairColorOptions"),
                ["eyeColorOptions"] = Text(item, "eyeColorOptions"),
                ["colorScheme"] = Text(item, "colorScheme"),
                ["manufacturer"] = Text(item, "manufacturer")
            },
            ["imageUrls"] = new JsonArray(Strings(item, "imageUrls")
                .Select(url => (JsonNode)JsonValue.Create(url))
                .ToArray()),
            ["halfHumanTraits"] = new JsonArray((Object(item, "halfHumanTableEntries") ?? [])
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["speciesName"] = entry.Key,
                    ["traitName"] = entry.Value is JsonValue value && value.TryGetValue<string>(out var trait)
                        ? trait
                        : null
                })
                .ToArray())
        };

        return With(mapped, Provenance(item));
    }

    /// <summary>
    /// The legacy record spells a free choice of ability as the pseudo-ability
    /// "Any one" / "Any two" / "Any four"; the target model splits that out into
    /// a separate count so a character builder does not have to parse English.
    /// </summary>
    private static JsonNode AbilityIncrease(JsonObject increase)
    {
        var abilities = Strings(increase, "abilities").ToList();
        var amount = Int(increase, "amount");

        var anyCount = abilities.Count == 1
            ? abilities[0] switch
            {
                "Any one" => 1,
                "Any two" => 2,
                "Any three" => 3,
                "Any four" => 4,
                "Any five" => 5,
                "Any six" => 6,
                _ => (int?)null
            }
            : null;

        return new JsonObject
        {
            ["amount"] = amount,
            ["abilities"] = anyCount is null
                ? new JsonArray(abilities.Select(ability => (JsonNode)JsonValue.Create(Lower(ability))).ToArray())
                : null,
            ["anyAbilityCount"] = anyCount
        };
    }

    // ------------------------------------------------------------- background

    private static JsonObject Background(JsonObject item)
    {
        var name = Text(item, "name")!;

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["lore"] = Text(item, "flavorText"),
            ["skillProficiencies"] = Text(item, "skillProficiencies"),
            ["toolProficiencies"] = Text(item, "toolProficiencies"),
            ["languageProficiencies"] = Text(item, "languages"),
            ["startingEquipment"] = Text(item, "equipment"),
            ["feature"] = new JsonObject
            {
                ["name"] = Text(item, "featureName"),
                ["description"] = Text(item, "featureText")
            },
            ["suggestedCharacteristics"] = Text(item, "suggestedCharacteristics"),
            ["personalityTraitOptions"] = RollTable(item, "personalityTraitOptions"),
            ["idealOptions"] = RollTable(item, "idealOptions"),
            ["bondOptions"] = RollTable(item, "bondOptions"),
            ["flawOptions"] = RollTable(item, "flawOptions"),
            ["featOptions"] = RollTable(item, "featOptions"),
            ["variant"] = new JsonObject
            {
                ["name"] = Text(item, "flavorName"),
                ["description"] = Text(item, "flavorDescription"),
                ["options"] = RollTable(item, "flavorOptions")
            }
        };

        return With(mapped, Provenance(item));
    }

    private static JsonNode? RollTable(JsonObject item, string field) =>
        Array(item, field) is { } rows
            ? new JsonArray(rows
                .OfType<JsonObject>()
                .Select(row => (JsonNode)new JsonObject
                {
                    ["roll"] = Int(row, "roll"),
                    ["name"] = Text(row, "name"),
                    ["description"] = Text(row, "description")
                })
                .ToArray())
            : null;

    // ------------------------------------------------------------------- feat

    private static JsonObject Feat(JsonObject item)
    {
        var name = Text(item, "name")!;

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["prerequisite"] = Text(item, "prerequisite"),
            ["abilityScoreIncreases"] = new JsonArray(Strings(item, "attributesIncreased")
                .Select(ability => (JsonNode)JsonValue.Create(Lower(ability)))
                .ToArray()),
            ["description"] = Text(item, "text")
        };

        return With(mapped, Provenance(item));
    }

    // ------------------------------------------------------------------ power

    private static JsonObject Power(JsonObject item)
    {
        var name = Text(item, "name")!;

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["powerType"] = Lower(Text(item, "powerType")!),
            ["level"] = Int(item, "level"),
            ["forceAlignment"] = Lower(Text(item, "forceAlignment")!),
            ["castingTime"] = new JsonObject
            {
                ["period"] = CamelCase(Text(item, "castingPeriod")!),
                ["text"] = Text(item, "castingPeriodText")
            },
            ["range"] = Text(item, "range"),
            ["duration"] = Text(item, "duration"),
            ["concentration"] = Bool(item, "concentration"),
            ["prerequisite"] = Text(item, "prerequisite"),
            ["description"] = Text(item, "description")
        };

        return With(mapped, Provenance(item));
    }

    // -------------------------------------------------------------- equipment

    private static JsonObject Equipment(JsonObject item)
    {
        var name = Text(item, "name")!;
        var weaponClassification = Text(item, "weaponClassification");
        var armorClassification = Text(item, "armorClassification");
        var damageDice = Int(item, "damageNumberOfDice") ?? 0;

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["category"] = CamelCase(Text(item, "equipmentCategory")!),
            ["costInCredits"] = Int(item, "cost"),
            ["weight"] = ParseWeight(Text(item, "weight")!),
            ["description"] = Text(item, "description"),
            ["properties"] = new JsonArray(Strings(item, "properties")
                .Select(property => (JsonNode)JsonValue.Create(property))
                .ToArray()),
            ["weaponClassification"] = weaponClassification is null or "Unknown"
                ? null
                : CamelCase(weaponClassification),
            ["damage"] = damageDice == 0
                ? null
                : new JsonObject
                {
                    ["numberOfDice"] = damageDice,
                    ["dieFaces"] = Int(item, "damageDieType"),
                    ["type"] = Lower(Text(item, "damageType")!)
                },
            ["armorClassification"] = armorClassification is null or "Unknown"
                ? null
                : Lower(armorClassification),
            ["armorClass"] = Text(item, "ac"),
            ["stealthDisadvantage"] = Bool(item, "stealthDisadvantage")
        };

        return With(mapped, Provenance(item));
    }

    // ---------------------------------------------------------------- monster

    private static JsonObject Monster(JsonObject item)
    {
        var name = Text(item, "name")!;

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["size"] = Lower(Text(item, "size")!),
            ["types"] = new JsonArray(Strings(item, "types")
                .Select(type => (JsonNode)JsonValue.Create(Lower(type)))
                .ToArray()),
            ["alignment"] = Text(item, "alignment"),
            ["armor"] = new JsonObject
            {
                ["class"] = Int(item, "armorClass"),
                ["type"] = Text(item, "armorType")
            },
            ["hitPoints"] = new JsonObject
            {
                ["average"] = Int(item, "hitPoints"),
                ["roll"] = Text(item, "hitPointRoll")
            },
            ["speed"] = new JsonObject
            {
                ["walk"] = Int(item, "speed"),
                ["text"] = Text(item, "speeds")
            },
            ["abilities"] = new JsonObject
            {
                ["strength"] = AbilityScore(item, "strength"),
                ["dexterity"] = AbilityScore(item, "dexterity"),
                ["constitution"] = AbilityScore(item, "constitution"),
                ["intelligence"] = AbilityScore(item, "intelligence"),
                ["wisdom"] = AbilityScore(item, "wisdom"),
                ["charisma"] = AbilityScore(item, "charisma")
            },
            ["savingThrows"] = StringArray(item, "savingThrows"),
            ["skills"] = StringArray(item, "skills"),
            ["senses"] = StringArray(item, "senses"),
            ["languages"] = StringArray(item, "languages"),
            ["damageVulnerabilities"] = DamageAffinity(item, "damageVulnerabilities"),
            ["damageResistances"] = DamageAffinity(item, "damageResistances"),
            ["damageImmunities"] = DamageAffinity(item, "damageImmunities"),
            ["conditionImmunities"] = new JsonObject
            {
                ["conditions"] = new JsonArray(Strings(item, "conditionImmunities")
                    .Select(condition => (JsonNode)JsonValue.Create(Lower(condition)))
                    .ToArray()),
                ["other"] = StringArray(item, "conditionImmunitiesOther")
            },
            ["challengeRating"] = Text(item, "challengeRating"),
            ["experiencePoints"] = Int(item, "experiencePoints"),
            ["behaviors"] = new JsonArray(Array(item, "behaviors")!
                .OfType<JsonObject>()
                .Select(Behavior)
                .ToArray()),
            ["flavorText"] = Text(item, "flavorText"),
            ["sectionText"] = Text(item, "sectionText")
        };

        return With(mapped, Provenance(item));
    }

    private static JsonNode AbilityScore(JsonObject item, string ability) => new JsonObject
    {
        ["score"] = Int(item, ability),
        ["modifier"] = Int(item, ability + "Modifier")
    };

    private static JsonNode? StringArray(JsonObject item, string field) =>
        Array(item, field) is null
            ? null
            : new JsonArray(Strings(item, field)
                .Select(value => (JsonNode)JsonValue.Create(value))
                .ToArray());

    private static JsonNode DamageAffinity(JsonObject item, string field) => new JsonObject
    {
        ["types"] = new JsonArray(Strings(item, field)
            .Select(type => (JsonNode)JsonValue.Create(Lower(type)))
            .ToArray()),
        ["other"] = StringArray(item, field + "Other")
    };

    private static JsonNode Behavior(JsonObject behavior)
    {
        var description = Text(behavior, "description");
        var withLinks = Text(behavior, "descriptionWithLinks");
        var damageType = Text(behavior, "damageType");
        var averageDamage = Text(behavior, "damage");

        return new JsonObject
        {
            ["name"] = Text(behavior, "name"),
            ["behaviorType"] = CamelCase(Text(behavior, "monsterBehaviorType")!),
            ["description"] = description,
            ["descriptionWithLinks"] = withLinks == description ? null : withLinks,
            ["attackType"] = CamelCase(Text(behavior, "attackType")!),
            ["attackBonus"] = Int(behavior, "attackBonus"),
            ["usageLimit"] = Text(behavior, "restrictions"),
            ["range"] = Text(behavior, "range"),
            ["targets"] = Text(behavior, "numberOfTargets"),
            ["averageDamage"] = string.IsNullOrWhiteSpace(averageDamage) ? null : int.Parse(averageDamage),
            ["damageRoll"] = Text(behavior, "damageRoll"),
            ["damageType"] = damageType is null or "Unknown" ? null : Lower(damageType)
        };
    }

    // -------------------------------------------------------------- archetype

    private static JsonObject Archetype(JsonObject item)
    {
        var name = Text(item, "name")!;

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["className"] = Text(item, "className"),
            ["casterType"] = Lower(Text(item, "casterType")!),
            ["casterRatio"] = Number(item, "casterRatio"),
            ["classCasterType"] = Lower(Text(item, "classCasterType")!),
            ["description"] = Text(item, "text"),
            ["progression"] = Progression(Object(item, "leveledTable"))
        };

        return With(mapped, Provenance(item));
    }

    private static JsonNode? Progression(JsonObject? table) =>
        table is null
            ? null
            : new JsonArray(table
                .Where(row => int.TryParse(row.Key, out _))
                .OrderBy(row => int.Parse(row.Key))
                .Select(row => (JsonNode)new JsonObject
                {
                    ["level"] = int.Parse(row.Key),
                    ["entries"] = new JsonArray((row.Value as JsonArray ?? [])
                        .OfType<JsonObject>()
                        .Select(cell => (JsonNode)new JsonObject
                        {
                            ["label"] = Text(cell, "key"),
                            ["value"] = Text(cell, "value")
                        })
                        .ToArray())
                })
                .ToArray());

    // ------------------------------------------------------------------ class

    /// <summary>
    /// A class record, which is the widest in the archive: fifty-odd fields
    /// covering prose, proficiencies, starting equipment and a twenty-row
    /// level table.
    /// <para>
    /// Two shapes need a decision the other content types never forced.
    /// </para>
    /// <para>
    /// The proficiency arrays are not lists. The scrape split one printed line
    /// on its commas, so the monk's "martial vibroweapons that lack the
    /// dexterity, heavy, special, and two-handed properties" arrives as four
    /// elements, three of which are sentence fragments. Joining them back with
    /// ", " reproduces the printed line exactly and is lossless; treating them
    /// as four proficiencies would not be. Saving throws and the skill choice
    /// list are genuine lists in the source and stay lists.
    /// </para>
    /// <para>
    /// The level table arrives as an object keyed by level, each value an
    /// object keyed by column heading. Property order in that inner object is
    /// the printed column order — <c>levelChangeHeadersJson</c> holds the same
    /// order, and it agrees with the row keys for all ten classes, so the rows
    /// are read directly and the stringified field is dropped with the rest of
    /// the <c>*Json</c> duplicates. Three columns every class prints get
    /// fields of their own; the rest become labelled entries, because no two
    /// classes share them.
    /// </para>
    /// </summary>
    private static JsonObject Class(JsonObject item)
    {
        var name = Text(item, "name")!;
        var skillOptions = Strings(item, "skillChoicesList").ToList();

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["summary"] = Text(item, "summary"),
            ["primaryAbility"] = Lower(Text(item, "primaryAbility")!),
            ["hitPoints"] = new JsonObject
            {
                ["dieFaces"] = Int(item, "hitDiceDieType"),
                ["atFirstLevel"] = Text(item, "hitPointsAtFirstLevel"),
                ["atFirstLevelValue"] = Int(item, "hitPointsAtFirstLevelNumber"),
                ["atHigherLevels"] = Text(item, "hitPointsAtHigherLevels"),
                ["atHigherLevelsAverage"] = Int(item, "hitPointsAtHigherLevelsNumber")
            },
            ["proficiencies"] = new JsonObject
            {
                ["armor"] = ProficiencyLine(item, "armorProficiencies"),
                ["weapons"] = ProficiencyLine(item, "weaponProficiencies"),
                ["tools"] = ProficiencyLine(item, "toolProficiencies"),
                ["savingThrows"] = new JsonArray(Strings(item, "savingThrows")
                    .Select(ability => (JsonNode)JsonValue.Create(Lower(ability)))
                    .ToArray()),
                ["skills"] = new JsonObject
                {
                    ["choose"] = Int(item, "numSkillChoices"),

                    // "Any" is the operative's entire list: the class picks
                    // from every skill there is. Storing that as a one-element
                    // list of the literal string "Any" would make it look like
                    // a skill named Any, so the list is omitted instead and an
                    // absent list means the choice is unrestricted.
                    ["from"] = skillOptions is ["Any"]
                        ? null
                        : new JsonArray(skillOptions
                            .Select(skill => (JsonNode)JsonValue.Create(skill))
                            .ToArray()),
                    ["text"] = Text(item, "skillChoices")
                }
            },
            ["multiclassProficiencies"] = ProficiencyLine(item, "multiClassProficiencies"),

            // The equipment lines are already markdown bullets in the archive,
            // so they are rejoined into one markdown block rather than kept as
            // an array the renderer would have to reassemble anyway.
            ["startingEquipment"] = JoinLines(Strings(item, "equipmentLines")),
            ["startingWealth"] = Text(item, "startingWealthVariant"),
            ["casterType"] = Lower(Text(item, "casterType")!),
            ["casterRatio"] = Number(item, "casterRatio"),
            ["archetypeLabel"] = Text(item, "archetypeFlavorName"),
            ["archetypeIntroduction"] = Text(item, "archetypeFlavorText"),
            ["lore"] = Text(item, "flavorText"),
            ["creatingCharacter"] = Text(item, "creatingText"),
            ["quickBuild"] = Text(item, "quickBuildText"),

            // classFeatureText2 is empty for all ten classes; it is the second
            // column of a two-column layout that no class overflowed into.
            ["description"] = Text(item, "classFeatureText"),
            ["imageUrls"] = new JsonArray(Strings(item, "imageUrls")
                .Select(url => (JsonNode)JsonValue.Create(url))
                .ToArray()),
            ["progression"] = ClassProgression(Object(item, "levelChanges"))
        };

        return With(mapped, Provenance(item));
    }

    /// <summary>
    /// One printed proficiency line, rebuilt from the commas the scrape split
    /// it on. "None" is the archive's way of writing an empty line, so it
    /// becomes null and the field is pruned away.
    /// </summary>
    private static JsonNode? ProficiencyLine(JsonObject item, string field)
    {
        var parts = Strings(item, field).ToList();

        return parts is [] or ["None"]
            ? null
            : JsonValue.Create(string.Join(", ", parts));
    }

    private static JsonNode? JoinLines(IEnumerable<string> lines)
    {
        var joined = string.Join("\n", lines);

        return string.IsNullOrWhiteSpace(joined) ? null : JsonValue.Create(joined);
    }

    /// <summary>
    /// Columns the level table prints that do not become labelled entries: the
    /// level and the features get fields of their own, and the printed "Level"
    /// column ("1st", "2nd") is a rendering of the row's own key, so it is
    /// dropped as a duplicate the way every other stringified copy is.
    /// </summary>
    private static readonly HashSet<string> ClassTableFixedColumns =
        new(StringComparer.Ordinal) { "Level", "Proficiency Bonus", "Features" };

    private static JsonNode? ClassProgression(JsonObject? table) =>
        table is null
            ? null
            : new JsonArray(table
                .Where(row => int.TryParse(row.Key, out _))
                .OrderBy(row => int.Parse(row.Key))
                .Select(row => (JsonNode)ClassProgressionRow(int.Parse(row.Key), row.Value as JsonObject))
                .ToArray());

    private static JsonObject ClassProgressionRow(int level, JsonObject? row)
    {
        row ??= [];

        return new JsonObject
        {
            ["level"] = level,
            ["proficiencyBonus"] = ProficiencyBonus(Text(row, "Proficiency Bonus")),
            ["features"] = new JsonArray(SplitFeatures(Text(row, "Features"))
                .Select(feature => (JsonNode)JsonValue.Create(feature))
                .ToArray()),
            ["entries"] = new JsonArray(row
                .Where(cell => !ClassTableFixedColumns.Contains(cell.Key))
                .Select(cell => (JsonNode)new JsonObject
                {
                    ["label"] = cell.Key,
                    ["value"] = cell.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        ? text
                        : null
                })
                .ToArray())
        };
    }

    /// <summary>
    /// "+3" becomes 3. The column is written with its sign because it is a
    /// bonus, and it is stored as a number because a character sheet adds it.
    /// </summary>
    private static int? ProficiencyBonus(string? printed) =>
        printed is not null && int.TryParse(printed.TrimStart('+'), out var bonus)
            ? bonus
            : null;

    /// <summary>
    /// The Features column is a comma-separated list of what arrives at this
    /// level. Splitting is all that happens here: a name that no feature
    /// document matches — "Ability Score Improvement", "Approach feature",
    /// "Brutal Critical (two dice)" — is what the book prints and is kept as
    /// printed. Cells that hold nothing but a lost character survive this step
    /// and are dropped by the repair stage, which is where corruption is
    /// handled.
    /// </summary>
    private static IEnumerable<string> SplitFeatures(string? printed) =>
        string.IsNullOrWhiteSpace(printed)
            ? []
            : printed
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------ class improvement

    private static readonly Dictionary<string, string> ImprovementLabels = new(StringComparer.Ordinal)
    {
        ["class"] = "Class Improvement",
        ["multiclass"] = "Multiclass Improvement",
        ["splashclass"] = "Splashclass Improvement"
    };

    /// <summary>
    /// One of the three per-class improvement rules. The archive stores the
    /// class name in <c>name</c> and nothing else identifying, so both the key
    /// and the display name are rebuilt from the class and the kind: ten
    /// records called "Berserker" in three files would otherwise collide into
    /// one slug and read as three copies of the same entry in a list.
    /// </summary>
    private static JsonObject ClassImprovement(JsonObject item, string improvementType)
    {
        var className = Text(item, "name")!;

        var mapped = new JsonObject
        {
            ["key"] = Slug(className, improvementType, "improvement"),
            ["name"] = $"{className} {ImprovementLabels[improvementType]}",
            ["className"] = className,
            ["improvementType"] = improvementType,
            ["prerequisite"] = Text(item, "prerequisite"),
            ["description"] = Text(item, "description")
        };

        return With(mapped, Provenance(item));
    }

    // ---------------------------------------------------------------- feature

    private static JsonObject Feature(JsonObject item)
    {
        var name = Text(item, "name")!;
        var grantedBy = Text(item, "source")!;
        var grantedByName = Text(item, "sourceName")!;
        var level = Int(item, "level");

        return new JsonObject
        {
            ["key"] = Slug(grantedBy, grantedByName, name, level?.ToString()),
            ["name"] = name,
            ["grantedBy"] = Lower(grantedBy),
            ["grantedByName"] = grantedByName,
            ["level"] = level,
            ["description"] = Text(item, "text")
        };
    }

    // -------------------------------------------------------- combat options
    //
    // The six combat-option types share a problem the other eight do not have:
    // the archive stores their mechanics as one prose blob, and the structure
    // that matters is written *inside* that blob rather than beside it. A
    // fighting style's benefits are a markdown bullet list; a lightsaber form's
    // two halves are two paragraphs; a maneuver's cost is a clause. The
    // schemas model those parts as fields, so this mapper has to find them.
    //
    // That is still mapping rather than repair, and the distinction is worth
    // stating because it is the line the rest of this file holds. Each rule
    // below keys off a marker the source itself prints — a "- " bullet, a
    // blank line, the italic "**Prerequisite:**" run-in, the literal sentence
    // "As a part of the bonus action to adopt this form" — and reproduces the
    // text it finds byte for byte. Nothing here rewrites a value, supplies a
    // missing one, or decides what a sentence means. Where the archive is
    // simply wrong, it stays wrong here and is caught by a test, exactly as
    // the monster with a challenge rating of "CR" is.

    /// <summary>
    /// The archive's CRLF line endings, normalised to LF.
    /// </summary>
    /// <remarks>
    /// This is the one liberty taken with the bytes, and it is unavoidable: a
    /// bullet, a paragraph break and a run-in heading are all defined in terms
    /// of line boundaries, so the boundaries have to be spelled one way before
    /// any of them can be found. It changes no character a reader sees.
    /// </remarks>
    private static string Lines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace('\r', '\n');

    /// <summary>
    /// The italic prerequisite line the books print above an entry, as in
    /// <c>_**Prerequisite:** The ability to cast force powers_</c>. Three of
    /// the 219 combat options carry one. It is lifted into a field of its own
    /// because a class builder has to be able to filter on it, and because
    /// leaving it inside the description would print a prerequisite in the
    /// middle of the rules text on any page that renders the two separately.
    /// </summary>
    private static readonly Regex PrerequisiteRunIn = new(
        @"^_\*\*Prerequisite:\*\*\s*(?<value>.+?)_\s*\n",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits the run-in prerequisite off the front of an entry, returning it
    /// and whatever follows.
    /// </summary>
    private static (string? Prerequisite, string Body) TakePrerequisite(string text)
    {
        var match = PrerequisiteRunIn.Match(text);

        return match.Success
            ? (match.Groups["value"].Value.Trim(), text[match.Length..])
            : (null, text);
    }

    /// <summary>
    /// Every maneuver whose text has the player spend a die says so with one
    /// of two phrasings: "expend a superiority die" or "expend and roll one
    /// superiority die". 109 of the 119 match; the ten that do not are the
    /// tiered upgrades, which cost nothing of their own, plus Effective
    /// Flanking, whose printed text has the player roll a die without
    /// expending it.
    /// </summary>
    private static readonly Regex ExpendsSuperiorityDie = new(
        @"expend(?:ing)?(?:\s+and\s+roll(?:ing)?)?\s+(?:a|one)\s+superiority\s+(?:die|dice)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The parenthesised tier on an upgraded maneuver's name. "Administer Aid
    /// (Improved)" is the same maneuver as "Administer Aid", one tier up, and
    /// the base name is the only machine-readable statement of that anywhere
    /// in the record: the upgrade's own prerequisite names the tier below it,
    /// which for a third tier is not the base at all.
    /// </summary>
    private static readonly Regex ManeuverTier = new(
        @"\s*\((?:Improved|Greater)\)$",
        RegexOptions.CultureInvariant);

    private static JsonObject Maneuver(JsonObject item)
    {
        var name = Text(item, "name")!;
        var description = Text(item, "description");
        var maneuverType = Text(item, "type");
        var baseName = ManeuverTier.Replace(name, string.Empty);

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["maneuverType"] = maneuverType is null ? null : Lower(maneuverType),

            // Always written, including the zero: a maneuver that costs
            // nothing is a fact about it, not a missing value, and Prune would
            // drop the field if it were null. JsonValue keeps 0 either way.
            ["superiorityDice"] =
                description is not null && ExpendsSuperiorityDie.IsMatch(description) ? 1 : 0,

            ["prerequisite"] = Text(item, "prerequisite"),
            ["improves"] = baseName == name ? null : baseName,
            ["description"] = description
        };

        return With(mapped, Provenance(item));
    }

    /// <summary>
    /// Fighting styles, fighting masteries, weapon focuses and weapon
    /// supremacies are all printed the same way: a sentence or two of lead-in
    /// ending in a colon, then a markdown bullet list of the benefits. All 80
    /// of them follow it exactly, with no prose after the last bullet, which
    /// is what makes splitting on the first bullet safe rather than lossy.
    /// </summary>
    private static JsonObject Bulleted(JsonObject item, string field, JsonObject? extra = null)
    {
        var name = Text(item, "name")!;
        var (prerequisite, body) = TakePrerequisite(Lines(Text(item, field)!));

        var lines = body.Split('\n');
        var firstBullet = System.Array.FindIndex(lines, line => line.StartsWith("- ", StringComparison.Ordinal));

        var lead = firstBullet < 0
            ? body.Trim()
            : string.Join("\n", lines[..firstBullet]).Trim();

        var benefits = firstBullet < 0
            ? []
            : lines[firstBullet..]
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..].Trim())
                .ToArray();

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name
        };

        if (extra is not null)
        {
            With(mapped, extra);
        }

        mapped["prerequisite"] = prerequisite;
        mapped["description"] = lead;
        mapped["benefits"] = new JsonArray(benefits
            .Select(benefit => (JsonNode)JsonValue.Create(benefit))
            .ToArray());

        return With(mapped, Provenance(item));
    }

    /// <summary>
    /// The eight weapon groups, spelled as they appear in an entry's name once
    /// the "Focus" or "Supremacy" suffix is removed. Three of them carry the
    /// word "Weapon" in the printed name and five do not, which is why the
    /// group is read from a table rather than derived by lower-casing.
    /// </summary>
    private static readonly Dictionary<string, string> WeaponGroups = new(StringComparer.Ordinal)
    {
        ["Blade"] = "blade",
        ["Carbine"] = "carbine",
        ["Crushing Weapon"] = "crushing",
        ["Heavy Weapon"] = "heavy",
        ["Polearm"] = "polearm",
        ["Rifle"] = "rifle",
        ["Sidearm"] = "sidearm",
        ["Trip Weapon"] = "trip"
    };

    private static JsonObject WeaponGrouped(JsonObject item, string suffix)
    {
        var name = Text(item, "name")!;
        var group = name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;

        return Bulleted(item, "description", new JsonObject
        {
            // An unrecognised group is left as the raw name so the schema's
            // enum rejects it loudly. Silently mapping it to null would drop
            // the field and let a new weapon group validate as a focus that
            // applies to nothing.
            ["weaponGroup"] = WeaponGroups.TryGetValue(group, out var mapped) ? mapped : group
        });
    }

    /// <summary>
    /// The sentence a lightsaber form uses to tie an effect to the bonus
    /// action that adopts it. Nine of the twenty open with it. It is matched
    /// as a literal rather than paraphrased because it is the source's own
    /// statement of the timing, and a reviewer has to be able to find it on
    /// the page.
    /// </summary>
    private const string FormAdoptionClause =
        "As a part of the bonus action to adopt this form";

    private static JsonObject LightsaberForm(JsonObject item)
    {
        var name = Text(item, "name")!;
        var (prerequisite, body) = TakePrerequisite(Lines(Text(item, "description")!));

        var effects = body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(paragraph => (JsonNode)new JsonObject
            {
                ["timing"] = paragraph.StartsWith(FormAdoptionClause, StringComparison.Ordinal)
                    ? "onAdopt"
                    : "active",
                ["description"] = paragraph
            })
            .ToArray();

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["prerequisite"] = prerequisite,
            ["effects"] = new JsonArray(effects)
        };

        return With(mapped, Provenance(item));
    }

    // ---------------------------------------------------------- enhanced item

    /// <summary>
    /// An enhanced item: a specific artefact, a modification, an augmentation or
    /// a consumable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two groups of legacy fields are dropped beyond the usual ones. The first
    /// is the ten <c>*Type</c> discriminators - <c>enhancedWeaponType</c>,
    /// <c>itemModificationType</c> and eight more - of which at most one is ever
    /// set on a record and all ten are "None" on more than half of them. They
    /// say less than <c>subtype</c>, which is populated on every record that has
    /// a kind at all, so <c>subtype</c> is the one that is kept.
    /// </para>
    /// <para>
    /// The second is the four spellings of rarity. <c>rarityOptions</c> is a
    /// one-element array on all 1,918 records, <c>rarityOptionsJson</c> is its
    /// stringified duplicate, <c>rarityText</c> is the same value with
    /// inconsistent casing, and <c>searchableRarity</c> is a display artefact of
    /// the old site's search box. The array is the one the target model keeps,
    /// collapsed to the scalar the data always was.
    /// </para>
    /// <para>
    /// <c>valueText</c> is dropped because it is null on every one of the 1,918
    /// records: no enhanced item in the corpus has a price. Rarity is what
    /// stands in for one, which is why the schema requires it.
    /// </para>
    /// </remarks>
    private static JsonObject EnhancedItem(JsonObject item)
    {
        var name = Text(item, "name")!;
        var subtype = Text(item, "subtype");

        var mapped = new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["itemType"] = CamelCase(Text(item, "type")!),
            ["rarity"] = SingleRarity(item),
            ["requiresAttunement"] = Bool(item, "requiresAttunement"),
            ["subtype"] = subtype is null ? null : Lower(subtype),
            ["prerequisite"] = Text(item, "prerequisite"),
            ["description"] = Text(item, "text")
        };

        return With(mapped, Provenance(item));
    }

    /// <summary>
    /// The record's one rarity, or null when it does not have exactly one. Null
    /// fails validation, which is the point: a record with two rarities would
    /// need an array and a decision about what a list page sorts on, and this
    /// mapping must not quietly pick one.
    /// </summary>
    private static JsonNode? SingleRarity(JsonObject item)
    {
        var options = Array(item, "rarityOptions");

        if (options is null || options.Count != 1 ||
            options[0] is not JsonValue value ||
            !value.TryGetValue<string>(out var rarity))
        {
            return null;
        }

        return Lower(rarity);
    }

    // ------------------------------------------------------------- properties

    /// <summary>
    /// A weapon or armour property glossary entry. One mapping serves both
    /// content types: the records are the same shape, and which glossary an
    /// entry belongs to is decided by the file it is in rather than by anything
    /// in the record.
    /// </summary>
    /// <remarks>
    /// <c>Provenance</c> is deliberately not used. It would derive a
    /// <c>sourceKey</c> of "none" from a <c>contentSource</c> of "None", and a
    /// property that cites a book called None is worse than one that cites no
    /// book at all - which is why neither property schema has the field.
    /// </remarks>
    private static JsonObject Property(JsonObject item)
    {
        var name = Text(item, "name")!;

        return new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["contentSet"] = ContentSet(Text(item, "contentType")),
            ["description"] = Text(item, "content")
        };
    }

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// A chapter of a book, or one optional variant rule.
    /// </summary>
    /// <remarks>
    /// The book and the kind come from the mapping key rather than the record,
    /// because the record does not carry them: all 76 have a contentSource of
    /// "None". VariantRule is attributed to the Expanded Content supplement
    /// because that is the book whose "Variant Rules" chapter prints them, and
    /// because every one of its records is already marked as expanded content.
    /// <para>
    /// Chapter keys carry the book's key as a prefix because seven chapter
    /// titles are printed in more than one book - all three print one called
    /// "Equipment" - and an unprefixed key would collide. Variant rule titles
    /// are unique across the corpus and take an unprefixed key.
    /// </para>
    /// </remarks>
    private static JsonObject Rule(JsonObject item, string sourceKey, string contentSet, string ruleType)
    {
        var name = Text(item, "chapterName")!;
        var isChapter = ruleType == "chapter";

        return new JsonObject
        {
            ["key"] = isChapter ? Slug(sourceKey, name) : Slug(name),
            ["name"] = name,
            ["sourceKey"] = sourceKey,
            ["contentSet"] = contentSet,
            ["ruleType"] = ruleType,
            ["chapterNumber"] = isChapter ? Int(item, "chapterNumber") : null,
            ["body"] = Text(item, "contentMarkdown")
        };
    }

    // ------------------------------------------------------- reference tables

    /// <summary>
    /// A standalone lookup table. Like the properties, these carry no usable
    /// provenance: the archive records "None" as the source, and unlike the rule
    /// chapters there is no file name to infer a book from, because the
    /// thirty-three tables come from at least three different ones.
    /// </summary>
    /// <remarks>
    /// <c>subject</c> is not mapped here. It is derived from the caption at
    /// import time to group the tables into a browsable list, and deriving it is
    /// a judgement rather than a rename - which is exactly the kind of thing
    /// this class does not do.
    /// </remarks>
    private static JsonObject ReferenceTable(JsonObject item)
    {
        var name = Text(item, "name")!;

        return new JsonObject
        {
            ["key"] = Slug(name),
            ["name"] = name,
            ["contentSet"] = ContentSet(Text(item, "contentType")),
            ["body"] = Text(item, "content")
        };
    }

    /// <summary>
    /// The content set a record belongs to. Wretched Hives is the case this
    /// exists for: its rule chapters record "None" while its 1,550 enhanced
    /// items record "Core", and they are the same book, so "None" resolves to
    /// core rather than being passed through as an enum value no schema accepts.
    /// </summary>
    private static string ContentSet(string? contentType) =>
        contentType == "ExpandedContent" ? "expanded-content" : "core";
}
