using Shouldly;
using Sw5e.Database.Tools.Legacy;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// The repair rules the legacy import applies, one test per rule.
/// </summary>
/// <remarks>
/// Every input below is taken verbatim from the archive. That matters: a rule
/// tested against a string invented to suit it proves only that the regular
/// expression compiles. The "left alone" block is the more important half —
/// it fixes the boundary between a character that can be recovered and one that
/// can only be guessed, and a change that pushes a case across that boundary
/// fails here rather than quietly inventing game content.
/// </remarks>
public sealed class LegacyTextTests
{
    [Theory]
    [InlineData("You can�t discern color", "You can't discern color")]
    [InlineData("I�m loyal to my friends", "I'm loyal to my friends")]
    [InlineData("resist the tank�s effects", "resist the tank's effects")]
    public void RestoresApostrophes(string archived, string repaired) =>
        LegacyText.Repair(archived).ShouldBe(repaired);

    [Theory]
    [InlineData("a new generation�for example, when", "a new generation—for example, when")]
    [InlineData("may have been related to�or the same", "may have been related to—or the same")]
    [InlineData("varying distances � sometimes by kilometers", "varying distances — sometimes by kilometers")]
    [InlineData("**Languages** �", "**Languages** —")]
    public void RestoresEmDashes(string archived, string repaired) =>
        LegacyText.Repair(archived).ShouldBe(repaired);

    [Theory]
    [InlineData("I have a �tell� that reveals", "I have a \"tell\" that reveals")]
    [InlineData("the proverb �unity is strength�.", "the proverb \"unity is strength\".")]
    public void RestoresBalancedQuotationMarks(string archived, string repaired) =>
        LegacyText.Repair(archived).ShouldBe(repaired);

    /// <summary>
    /// A quotation that ends a markdown table cell closes against the cell
    /// boundary rather than against a space. The Expanded Content backgrounds
    /// chapter writes its personality-trait tables that way, one quoted line
    /// per row.
    /// </summary>
    [Fact]
    public void RestoresQuotationMarksThatCloseAgainstATableCellBoundary() =>
        LegacyText.Repair("|3|always searching for that �special someone.�|")
            .ShouldBe("|3|always searching for that \"special someone.\"|");

    /// <summary>
    /// A replacement character that is a whole table cell is the em dash these
    /// tables use for "none". Both spellings appear: bare, and padded out with
    /// tabs to line the column up in the source.
    /// </summary>
    [Theory]
    [InlineData("|Level|Wealth|\r\n|1st|�|\r\n|2nd|1,000 cr|", "|Level|Wealth|\n|1st|—|\n|2nd|1,000 cr|")]
    [InlineData("|\tWretched\t\t|\t�\t|", "|\tWretched\t\t|\t—\t|")]
    public void RestoresTheEmDashAMarkdownTableUsesForNone(string archived, string repaired) =>
        LegacyText.Repair(archived).ShouldBe(repaired);

    /// <summary>
    /// A run of empty cells has to repair every cell, not every other one. The
    /// enhanced-item distribution tables are mostly empty cells, and a rule
    /// that consumed the shared pipe would leave half of them corrupt.
    /// </summary>
    [Fact]
    public void RestoresEveryCellInARunOfEmptyOnes() =>
        LegacyText.Repair("|1-4|6|3|�|�|�|�|9|")
            .ShouldBe("|1-4|6|3|—|—|—|—|9|");

    /// <summary>
    /// The source books open a section with a decorative initial, which the
    /// scraper read both as the decoration and as the first letter of the
    /// paragraph.
    /// </summary>
    [Theory]
    [InlineData("DDestiny plays a large role", "Destiny plays a large role")]
    [InlineData("WWhen you cast a power", "When you cast a power")]
    public void CollapsesADroppedCapitalDoubledByTheScrape(string archived, string repaired) =>
        LegacyText.Repair(archived).ShouldBe(repaired);

    [Fact]
    public void LeavesADoubledCapitalAloneInTheMiddleOfALine() =>
        LegacyText.Repair("the droid designated RRuk, of the RRuk line")
            .ShouldBe("the droid designated RRuk, of the RRuk line");

    /// <summary>
    /// The cases where the original character is genuinely unknowable. A rule
    /// that "fixed" any of these would be inventing content: a lost character
    /// before a space could be an em dash or an ellipsis and reads naturally as
    /// either, and a lost letter inside a proper noun has nothing to recover it
    /// from at all.
    /// </summary>
    [Theory]
    [InlineData("I can't help it� I'm a perfectionist.")]
    [InlineData("**Male Names.** Gliconn, Orcas, L�vern, Seelv�n")]
    [InlineData("**Female Names.** Kintik, Midwan, Siqsa, Ty�k")]
    public void LeavesUnrecoverableCharactersAlone(string archived)
    {
        var repaired = LegacyText.Repair(archived);

        LegacyText.ContainsUnrepairedLoss(repaired).ShouldBeTrue(
            $"'{archived}' was repaired; the original character is not recoverable from it.");
    }

    [Fact]
    public void DropsAFieldWhoseContentIsEntirelyLost()
    {
        LegacyText.IsTotalLoss("� �").ShouldBeTrue();
        LegacyText.Repair("� �").ShouldBeNull();
        LegacyText.Repair("").ShouldBeNull();
        LegacyText.Repair("   \r\n  ").ShouldBeNull();
    }

    [Fact]
    public void NormalisesTheArchivesLineEndingsAndBlankRuns() =>
        LegacyText.Repair("first\r\n\r\n\r\n\r\nsecond   \r\nthird\r\n")
            .ShouldBe("first\n\nsecond\nthird");

    /// <summary>
    /// Every page prints the document's own name as its heading, so a body that
    /// opens by repeating it shows the title twice. Both spellings the archive
    /// uses are recognised: the glossary's bare title, and a book chapter's
    /// numbered one.
    /// </summary>
    [Theory]
    [InlineData("#### Power Cell\nWeapons with this property", "Power Cell", "Weapons with this property")]
    [InlineData("#### Two-Handed\nThis weapon requires", "Two-Handed", "This weapon requires")]
    [InlineData("# Chapter 5: Equipment\n\nA character's", "Equipment", "A character's")]
    [InlineData("# Appendix A: Conditions\n\nConditions alter", "Appendix A: Conditions", "Conditions alter")]
    public void StripsALeadingHeadingThatOnlyRepeatsTheTitle(string body, string title, string expected) =>
        LegacyText.StripLeadingHeadingMatching(body, title).ShouldBe(expected);

    /// <summary>
    /// A heading that says something else is content, not a duplicate. The
    /// Player's Handbook's "What's Different" opens with a real subheading.
    /// </summary>
    [Fact]
    public void KeepsALeadingHeadingThatIsNotTheTitle() =>
        LegacyText.StripLeadingHeadingMatching(
            "### The Player's Handbook\nThis book mirrors", "Whats Different")
            .ShouldBe("### The Player's Handbook\nThis book mirrors");

    [Theory]
    [InlineData("AB-75 Bo-Rifle", "ab-75-bo-rifle")]
    [InlineData("Dispelling Dataport (Fine)", "dispelling-dataport-fine")]
    [InlineData("Appendix A: Conditions", "appendix-a-conditions")]
    [InlineData("XP and PB by Level", "xp-and-pb-by-level")]
    public void BuildsKeysTheSchemasKeyPatternAccepts(string name, string key) =>
        LegacyText.Slug(name).ShouldBe(key);

    [Fact]
    public void PrefixesAKeyWithItsBookWhenAskedTo() =>
        LegacyText.Slug("phb", "Equipment").ShouldBe("phb-equipment");
}
