using System.Text.Json.Nodes;
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
        _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "No mapping defined.")
    };

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
}
