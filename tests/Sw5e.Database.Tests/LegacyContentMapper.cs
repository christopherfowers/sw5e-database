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
}
