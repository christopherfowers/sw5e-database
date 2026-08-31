using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Guards the six combat-option types as a body of content rather than as
/// individual documents.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeedContentTests"/> already proves every one of these files is
/// valid, clean and internally consistent, and it would keep proving that if
/// the directories held four documents instead of 219. That is the failure
/// this class exists for. The combat options were imported wholesale from a
/// 2022 archive by a mapping that reads structure out of prose — bullets out
/// of a paragraph, a die cost out of a clause, a form's two halves out of a
/// blank line — and every one of those rules degrades quietly. A regex that
/// stops matching does not throw; it returns nothing, and the import writes
/// 219 documents that are each individually valid and collectively useless.
/// </para>
/// <para>
/// So the assertions here are about totals and distributions, taken from the
/// source books and written down as literals. If a document is dropped, if a
/// benefit list comes back empty, if every maneuver suddenly costs one die, or
/// if the whole set is replaced by a handful of samples, one of these numbers
/// changes and the build goes red.
/// </para>
/// </remarks>
public sealed class CombatOptionContentTests
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    /// <summary>
    /// The size of each combat-option type, as published. These are the counts
    /// the archive holds and the counts the site is expected to render; they
    /// are not derived from the directory, because a count read off the
    /// directory would agree with an empty directory.
    /// </summary>
    public static TheoryData<string, int> PublishedCounts =>
        new()
        {
            { "maneuver", 119 },
            { "fighting-style", 32 },
            { "fighting-mastery", 32 },
            { "lightsaber-form", 20 },
            { "weapon-focus", 8 },
            { "weapon-supremacy", 8 }
        };

    /// <summary>Every document of one content type, keyed by its name.</summary>
    private static IReadOnlyDictionary<string, JsonObject> Load(string contentType)
    {
        var directory = Path.Combine(ContentRoot, contentType);

        Directory.Exists(directory).ShouldBeTrue(
            $"No content directory at '{directory}'. The {contentType} type is " +
            "declared in the schemas and in the API's registry, so an absent " +
            "directory publishes an empty index on a page the navigation links to.");

        return Directory
            .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidOperationException($"{file} is not a JSON object."))
            .ToDictionary(document => Text(document, "name")!, StringComparer.Ordinal);
    }

    private static string? Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static IReadOnlyList<string> Strings(JsonObject item, string field) =>
        (item[field] as JsonArray ?? [])
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is not null)
            .Select(text => text!)
            .ToList();

    [Theory]
    [MemberData(nameof(PublishedCounts))]
    public void EveryCombatOptionInTheBooksIsPublished(string contentType, int expected)
    {
        Load(contentType).Count.ShouldBe(expected,
            $"content/{contentType} should hold every {contentType} the books print.");
    }

    [Fact]
    public void TheCombatOptionsTotalTwoHundredAndNineteenItems()
    {
        var total = PublishedCounts
            .Select(row => Load((string)row[0]).Count)
            .Sum();

        total.ShouldBe(219);
    }

    // ------------------------------------------------------------ maneuvers

    /// <summary>
    /// The three maneuver lists, with the number of maneuvers on each. The
    /// split matters mechanically — a class feature grants access to a named
    /// list — so a mapping that collapsed the three into one, or dropped the
    /// field and left every maneuver general, has to fail rather than produce
    /// 119 plausible documents.
    /// </summary>
    [Fact]
    public void ManeuversAreDistributedAcrossTheThreeLists()
    {
        var byType = Load("maneuver")
            .Values
            .GroupBy(document => Text(document, "maneuverType"))
            .ToDictionary(group => group.Key!, group => group.Count(), StringComparer.Ordinal);

        byType.ShouldBe(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["mental"] = 50,
            ["general"] = 39,
            ["physical"] = 30
        }, ignoreOrder: true);
    }

    /// <summary>
    /// 109 maneuvers spend a superiority die and ten do not. The ten are named
    /// rather than counted, because "ten cost nothing" would also be satisfied
    /// by ten arbitrary maneuvers having lost their cost.
    /// </summary>
    [Fact]
    public void OnlyTheUpgradesAndEffectiveFlankingCostNoSuperiorityDie()
    {
        var maneuvers = Load("maneuver");

        var free = maneuvers
            .Where(entry => Int(entry.Value, "superiorityDice") == 0)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        free.ShouldBe(
        [
            "Administer Aid (Greater)",
            "Administer Aid (Improved)",
            "Call to Arms (Improved)",
            "Commander's Strike (Greater)",
            "Commander's Strike (Improved)",
            // The one base maneuver among them: its printed text has the
            // player roll a superiority die without expending one.
            "Effective Flanking",
            "Effective Flanking (Greater)",
            "Effective Flanking (Improved)",
            "Parry (Improved)",
            "Sweeping Attack (Improved)"
        ]);

        maneuvers.Count(entry => Int(entry.Value, "superiorityDice") == 1).ShouldBe(109);
    }

    /// <summary>
    /// Every tiered maneuver points at the maneuver it upgrades, and that
    /// maneuver exists. This is the relationship the printed lists express
    /// through naming alone; recording it is most of what makes the type
    /// usable by anything other than a human reader.
    /// </summary>
    [Fact]
    public void EveryUpgradeNamesTheManeuverItImproves()
    {
        var maneuvers = Load("maneuver");

        var upgrades = maneuvers
            .Where(entry => Text(entry.Value, "improves") is not null)
            .ToList();

        upgrades.Count.ShouldBe(10);

        foreach (var (name, document) in upgrades)
        {
            var improved = Text(document, "improves")!;

            maneuvers.ContainsKey(improved).ShouldBeTrue(
                $"{name} improves '{improved}', which is not a published maneuver.");

            name.ShouldStartWith(improved);
        }
    }

    /// <summary>
    /// A maneuver cannot be its own prerequisite.
    /// </summary>
    /// <remarks>
    /// The archive prints exactly that for Call to Arms (Improved), whose
    /// prerequisite is recorded as "Call to Arms (Improved) maneuver". There is
    /// no third tier the clause could have meant, and every other second-tier
    /// maneuver in the corpus requires its own base tier, so the published
    /// document reads "Call to Arms maneuver". This assertion is what stops a
    /// later re-import from quietly reinstating a maneuver that can never be
    /// taken.
    /// </remarks>
    [Fact]
    public void NoManeuverIsItsOwnPrerequisite()
    {
        foreach (var (name, document) in Load("maneuver"))
        {
            var prerequisite = Text(document, "prerequisite");

            if (prerequisite is null)
            {
                continue;
            }

            prerequisite.ShouldNotBe($"{name} maneuver",
                $"{name} lists itself as its own prerequisite, so it can never be taken.");
        }
    }

    // ------------------------------------------- styles, masteries, focuses

    /// <summary>
    /// The four bulleted types keep their benefits as a list, not as a
    /// paragraph. Two things go wrong when the split fails, and they look
    /// nothing alike: the benefits come back empty and the whole entry
    /// collapses into the lead sentence, or the bullets are left in the lead
    /// and duplicated into the list. Both are checked.
    /// </summary>
    [Theory]
    [InlineData("fighting-style", 32)]
    [InlineData("fighting-mastery", 32)]
    [InlineData("weapon-focus", 8)]
    [InlineData("weapon-supremacy", 8)]
    public void BulletedCombatOptionsKeepTheirBenefitsAsAList(string contentType, int expected)
    {
        var documents = Load(contentType);

        documents.Count.ShouldBe(expected);

        foreach (var (name, document) in documents)
        {
            var description = Text(document, "description")!;
            var benefits = Strings(document, "benefits");

            benefits.ShouldNotBeEmpty($"{contentType}/{name} lists no benefits.");

            description.Contains("\n- ", StringComparison.Ordinal).ShouldBeFalse(
                $"{contentType}/{name} left its benefit bullets inside the description, " +
                "so a reader would be shown them twice.");

            foreach (var benefit in benefits)
            {
                benefit.ShouldNotStartWith("- ",
                    customMessage: $"{contentType}/{name} kept the markdown bullet marker inside a benefit.");
                benefit.Trim().ShouldNotBeEmpty();
            }
        }
    }

    /// <summary>
    /// Fighting masteries are the later-career counterpart of the styles and
    /// are written to be longer. 32 of each, and the masteries carry more
    /// benefits in total — which is only checkable because the benefits are a
    /// list rather than prose.
    /// </summary>
    [Fact]
    public void MasteriesGrantMoreThanStyles()
    {
        static int Benefits(string contentType) =>
            Load(contentType).Values.Sum(document => Strings(document, "benefits").Count);

        Benefits("fighting-mastery").ShouldBeGreaterThan(Benefits("fighting-style"));
    }

    /// <summary>
    /// Each of the eight weapon groups has exactly one focus and exactly one
    /// supremacy. A character picks a group, so a group with no entry is a
    /// choice that cannot be made and a group with two is a choice that cannot
    /// be resolved.
    /// </summary>
    [Theory]
    [InlineData("weapon-focus")]
    [InlineData("weapon-supremacy")]
    public void EveryWeaponGroupHasExactlyOneEntry(string contentType)
    {
        var groups = Load(contentType)
            .Values
            .Select(document => Text(document, "weaponGroup"))
            .Order(StringComparer.Ordinal)
            .ToList();

        groups.ShouldBe(
        [
            "blade", "carbine", "crushing", "heavy",
            "polearm", "rifle", "sidearm", "trip"
        ]);
    }

    // ------------------------------------------------------ lightsaber forms

    /// <summary>
    /// A form's effects are split by when they apply. Nine of the twenty do
    /// something as part of the bonus action that adopts them; the rest only
    /// grant a benefit for as long as they are held. A mapping that lost the
    /// distinction would leave every effect labelled the same way, which is
    /// what the second assertion catches.
    /// </summary>
    [Fact]
    public void LightsaberFormsSeparateAdoptionEffectsFromActiveOnes()
    {
        var forms = Load("lightsaber-form");

        forms.Count.ShouldBe(20);

        var timings = forms.Values
            .SelectMany(form => (form["effects"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(effect => Text(effect, "timing")!))
            .ToList();

        timings.Count(timing => timing == "onAdopt").ShouldBe(9);
        timings.Count(timing => timing == "active").ShouldBe(16);

        foreach (var (name, form) in forms)
        {
            var effects = (form["effects"] as JsonArray ?? []).OfType<JsonObject>().ToList();

            effects.ShouldNotBeEmpty($"lightsaber-form/{name} has no effects at all.");
            effects.Count.ShouldBeLessThanOrEqualTo(2);

            foreach (var effect in effects)
            {
                var description = Text(effect, "description")!;

                // The timing is read off this sentence, so the sentence has to
                // still be there for the label to be checkable against the page.
                if (Text(effect, "timing") == "onAdopt")
                {
                    description.ShouldStartWith("As a part of the bonus action to adopt this form");
                }
            }
        }
    }

    /// <summary>
    /// One form, in full, asserted field by field. The counts above prove the
    /// import ran; this proves it produced the right thing, and it is the
    /// assertion that fails if the paragraph split ever runs backwards.
    /// </summary>
    [Fact]
    public void ShiiChoFormIsPublishedWithBothOfItsHalves()
    {
        var form = Load("lightsaber-form")["Shii-Cho Form"];

        Text(form, "key").ShouldBe("shii-cho-form");
        Text(form, "sourceKey").ShouldBe("phb");
        Text(form, "contentSet").ShouldBe("core");

        var effects = (form["effects"] as JsonArray)!.OfType<JsonObject>().ToList();

        effects.Count.ShouldBe(2);

        Text(effects[0], "timing").ShouldBe("onAdopt");
        Text(effects[0], "description")!.ShouldContain("engage in Double- or Two-Weapon Fighting");

        Text(effects[1], "timing").ShouldBe("active");
        Text(effects[1], "description")!.ShouldContain("Strength saving throw");
    }

    /// <summary>
    /// The three combat options that carry a prerequisite carry it as a field,
    /// not as an italic line at the top of their rules text.
    /// </summary>
    [Fact]
    public void RunInPrerequisitesAreLiftedIntoTheirOwnField()
    {
        var expected = new (string ContentType, string Name, string Prerequisite)[]
        {
            ("lightsaber-form", "Aqinos Form", "The ability to cast tech powers"),
            ("fighting-style", "Formfighting Style", "The ability to cast force powers"),
            ("fighting-mastery", "Formfighting Mastery", "The ability to cast force powers")
        };

        foreach (var (contentType, name, prerequisite) in expected)
        {
            Text(Load(contentType)[name], "prerequisite").ShouldBe(prerequisite);
        }

        foreach (var contentType in PublishedCounts.Select(row => (string)row[0]))
        {
            foreach (var (name, document) in Load(contentType))
            {
                document.ToJsonString().ShouldNotContain("**Prerequisite:**",
                    customMessage: $"{contentType}/{name} still carries its prerequisite as a run-in " +
                    "heading inside its rules text.");
            }
        }
    }
}
