using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sw5e.Database.Tools.Legacy;

/// <summary>
/// Repairs the encoding damage baked into the 2022 legacy SW5e archive, and
/// tidies the whitespace the same scrape mangled.
/// </summary>
/// <remarks>
/// <para>
/// The archive was scraped from PDFs with a broken decoder, so every character
/// outside the scraper's code page was written out as U+FFFD REPLACEMENT
/// CHARACTER. The original character is not recoverable from the bytes, only
/// from context, so each rule below has to earn its place: it must describe a
/// shape that only one plausible character could have produced.
/// </para>
/// <para>
/// Where context cannot settle it, the answer is not a guess and it is not
/// U+FFFD either: it is a lookup, read out of the document the chapter is
/// printed from and recorded with that source named. <see
/// cref="NamesFromSource"/> is the one such table, and it exists because no
/// shape can tell you which vowel stood in <c>H?sk</c>. Inventing a letter
/// inside a proper noun silently invents game content; looking it up does not.
/// </para>
/// <para>
/// <b>This is a second implementation of a set of rules that also exists in
/// JavaScript</b>, as <c>scripts/lib/repair-text.mjs</c> in the sw5e-web
/// repository. That is not accidental duplication and it is not a shared
/// library waiting to be extracted. The two run at different points on
/// different inputs: the JavaScript one repairs the archive as the site builds
/// a dataset straight from it, and this one repairs the archive once, at
/// import time, so that the content set in this repository is already clean and
/// nothing downstream has to know the corruption ever existed. Content here is
/// the canonical, hand-maintainable form; a repair pass that ran over it on
/// every read would mean the file on disk and the document served were
/// different documents. The rules are kept in step by hand, and both sides
/// carry this note.
/// </para>
/// </remarks>
internal static partial class LegacyText
{
    internal const char ReplacementCharacter = '�';

    /// <summary>
    /// Contraction and possessive suffixes. A replacement character wedged
    /// between a letter and one of these, with a word boundary after it, can
    /// only have been an apostrophe: <c>can?t</c>, <c>the tank?s effects</c>.
    /// The trailing word boundary is what makes this safe: it stops the rule
    /// firing on a dash that happens to precede a word starting with the same
    /// letter, as in <c>strength?something</c>.
    /// </summary>
    [GeneratedRegex(@"(?<=\p{L})�(?=(?:t|s|d|m|re|ve|ll)\b)")]
    private static partial Regex Contraction();

    /// <summary>
    /// A balanced pair of replacement characters, opening after whitespace and
    /// closing before whitespace, punctuation or a table-cell boundary, with
    /// non-space content between them: <c>a ?tell? that</c>. Only a quotation
    /// mark pairs like that. The length cap keeps the pairing local, so two
    /// unrelated dashes far apart cannot be mistaken for a quotation.
    /// </summary>
    /// <remarks>
    /// The closing context includes <c>|</c> because the corpus quotes inside
    /// pipe tables — a background's personality-trait table is one quoted line
    /// per row — and a quotation that ends a cell is followed by the cell
    /// boundary rather than by a space.
    /// </remarks>
    [GeneratedRegex(@"(^|[\s([])�(?=\S)([^�\n]{0,80}?)(?<=\S)�(?=[\s.,;:!?)\]|]|$)",
        RegexOptions.Multiline)]
    private static partial Regex QuotePair();

    /// <summary>
    /// A replacement character that is the entire content of a markdown table
    /// cell. Only an em dash sets a cell to "none" in these tables, and the
    /// corpus is full of them: the starting-wealth table, the armour tables'
    /// stealth column, the tool-proficiency table's uses column, and the
    /// enhanced-item distribution tables where every zero is printed as a dash.
    /// </summary>
    /// <remarks>
    /// The cell boundaries are matched as lookaround rather than consumed, so
    /// a run of empty cells — <c>|1-4|6|3|?|?|?|?|9|</c> — repairs every one of
    /// them rather than every other one.
    /// </remarks>
    [GeneratedRegex(@"(?<=\|)([ \t]*)�([ \t]*)(?=\|)")]
    private static partial Regex TableCellDash();

    /// <summary>
    /// A replacement character standing alone after a space: a spaced em dash.
    /// Stat blocks write <c>Languages —</c> to mean "none", which is why the
    /// form that ends a line matters as much as the one between two words.
    /// </summary>
    [GeneratedRegex(@"(?<= )�(?=\s|$)")]
    private static partial Regex SpacedDash();

    /// <summary>
    /// A replacement character welded to the word before it, as in
    /// <c>generation?for example</c> or <c>cost many lives? and I would</c>.
    /// Every one of these is an em dash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What follows the dash is left exactly as the archive has it, because
    /// the source books are not consistent and both forms are deliberate. The
    /// Player's Handbook and the Expanded Content chapters set a space after
    /// the dash and none before it — <c>I can't help it— I'm a
    /// perfectionist.</c>, <c>whatever services you can afford— paying for
    /// food</c> — while Wretched Hives sets the pair closed:
    /// <c>insignia—the Kyr'bes—features</c>. Normalising either way would be
    /// an editorial change dressed up as a repair, so the rule inserts the
    /// character and nothing else.
    /// </para>
    /// <para>
    /// The left-hand guard is what keeps this off proper nouns: two
    /// non-space characters have to precede the dash, which excludes
    /// <c>L?vern</c>, <c>H?sk</c>, <c>F?ress</c> and <c>J?nsone</c>, where the
    /// lost letter sits second in a name that follows a space. Asterisks are
    /// in the class because the corpus welds dashes to markdown emphasis —
    /// <c>**insignia**?the</c> — and a digit is allowed on the right for
    /// <c>the final three scores?12, 10, and 8?to Charisma</c>. That same
    /// sentence is why a digit needs no second character before it: in
    /// <c>and 8?to Charisma</c> the dash follows a space and a single <c>8</c>,
    /// and a digit after a space is never the second letter of a name.
    /// </para>
    /// <para>
    /// A lone letter on the right is still excluded, which is what keeps the
    /// rule away from <c>Seelv?n</c> and <c>Ty?k</c>; those two are repaired
    /// by <see cref="NamesFromSource"/> before this rule ever sees them.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"(?:(?<=\d)|(?<=[\p{L}\d'’""*]{2}))�(?=(?:\p{L}{2}|\d|[aI]\b|[ ]\p{L}))")]
    private static partial Regex WeldedDash();

    /// <summary>
    /// Accented letters the scrape ate out of the Expanded Content species
    /// name tables, read back out of the document those tables are printed
    /// from and quoted here rather than inferred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No pattern can recover these. <c>H?sk</c> gives no clue which vowel
    /// stood there, and the chapter's own prose says only that kallerans use
    /// "accents on vowels". The source is
    /// <c>gmbinder.com/share/-LM7Sz_VhFOf0IYKXHQG</c> — "EC 02 - Species", the
    /// document sw5e.com's Expanded Content species chapter is printed from.
    /// </para>
    /// <para>
    /// Each entry carries enough of the name either side of the lost letter to
    /// be unambiguous, so a name repairs wherever it appears rather than only
    /// inside a table row.
    /// </para>
    /// </remarks>
    private static readonly (string Broken, string Repaired)[] NamesFromSource =
    [
        ("L�vern", "Lávern"),     // kalleran, male
        ("Seelv�n", "Seelván"),   // kalleran, male
        ("F�ress", "Fáress"),     // kalleran, female
        ("Ian�", "Iané"),         // kiffar, female
        ("H�sk", "Hâsk"),         // massassi, male
        ("Ty�k", "Tyûk"),         // massassi, female
        ("J�nsone", "Jánsone"),   // theelin, male
        ("F�niss", "Fániss")      // theelin, female
    ];

    /// <summary>
    /// A capital letter doubled at the start of a line and followed by lower
    /// case: <c>DDestiny plays a large role</c>, <c>WWhen you cast a power</c>.
    /// </summary>
    /// <remarks>
    /// These are drop caps. The source books open a section with a large
    /// decorative initial, and the scraper read it both as the decoration and
    /// as the first letter of the paragraph, so the letter came out twice. The
    /// shape is safe to collapse because no English word begins with a doubled
    /// capital followed by lower case, and the corpus bears that out: the rule
    /// matches eight places in the whole archive and every one of them is a
    /// drop cap — five variant rules, two chapters and one enhanced item.
    /// Anchoring to the start of a line is what keeps it away from the middle
    /// of a sentence, where a doubled capital would more likely be an
    /// abbreviation or an alien name.
    /// </remarks>
    [GeneratedRegex(@"(?<=^|\n)([A-Z])\1(?=[a-z])")]
    private static partial Regex DoubledInitial();

    [GeneratedRegex(@"\r\n?")]
    private static partial Regex LineEndings();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun();

    /// <summary>
    /// A level-four heading on the first line that only repeats a title. The
    /// weapon- and armour-property glossary entries each open with one.
    /// </summary>
    [GeneratedRegex(@"^\s*#{1,6}\s*(?<heading>[^\n]*?)\s*(?:\n|$)")]
    private static partial Regex LeadingHeading();

    /// <summary>
    /// Applies every repair rule, in the order that keeps them from competing.
    /// The name lookup runs first so that no dash rule can reach a lost letter
    /// that is already accounted for. Quotation pairs run next because a
    /// closing quote looks exactly like a welded dash once its partner has
    /// been rewritten, and the table-cell rule runs before the spaced-dash
    /// rule so that a cell padded with spaces is recognised as a cell rather
    /// than as a mid-sentence dash.
    /// </summary>
    private static string ApplyRules(string text)
    {
        foreach (var (broken, repaired) in NamesFromSource)
        {
            text = text.Replace(broken, repaired, StringComparison.Ordinal);
        }

        text = QuotePair().Replace(text, "$1\"$2\"");
        text = Contraction().Replace(text, "'");
        text = TableCellDash().Replace(text, "$1—$2");
        text = SpacedDash().Replace(text, "—");
        text = WeldedDash().Replace(text, "—");
        text = DoubledInitial().Replace(text, "$1");
        return text;
    }

    /// <summary>
    /// Repairs a value and normalises its whitespace. Returns null for a value
    /// that is empty, or whose content is entirely replacement characters, so
    /// callers can drop the field rather than write an empty shell.
    /// </summary>
    internal static string? Repair(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (IsTotalLoss(value))
        {
            return null;
        }

        var text = LineEndings().Replace(value, "\n");
        text = ApplyRules(text);
        text = TrailingSpace().Replace(text, "\n");
        text = BlankRun().Replace(text, "\n\n").Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// True when a value carried content that is now entirely gone: it holds
    /// nothing but replacement characters and whitespace. Such a field has no
    /// content left to show and must be dropped rather than rendered.
    /// </summary>
    internal static bool IsTotalLoss(string value) =>
        value.Contains(ReplacementCharacter, StringComparison.Ordinal) &&
        value.All(character => character == ReplacementCharacter || char.IsWhiteSpace(character));

    /// <summary>True when repair left at least one unrecoverable character behind.</summary>
    internal static bool ContainsUnrepairedLoss(string? value) =>
        value is not null && value.Contains(ReplacementCharacter, StringComparison.Ordinal);

    /// <summary>How many replacement characters a value still holds.</summary>
    internal static int UnrepairedCount(string? value) =>
        value is null ? 0 : value.Count(character => character == ReplacementCharacter);

    /// <summary>
    /// A "Chapter 8: " style prefix on a heading. The books print the number in
    /// the heading and record it separately in <c>chapterNumber</c>, so the two
    /// halves of the heading have to be recognised independently.
    /// </summary>
    [GeneratedRegex(@"^chapter\s+-?\d+\s*[:.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPrefix();

    /// <summary>
    /// Removes a leading markdown heading when it says nothing but the title
    /// the document already carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every page on the site prints the document's name as its own heading, so
    /// a body that opens by repeating it shows the title twice. That is what
    /// the archive does: each property glossary entry opens with
    /// "#### Power Cell", and each book chapter with "# Chapter 5: Equipment".
    /// </para>
    /// <para>
    /// Comparison ignores case, every non-alphanumeric character, and a leading
    /// "Chapter N:" — the number is kept in its own field, so a heading is a
    /// repeat of the title whether or not it carries one. A heading that says
    /// anything else is left alone: the Player's Handbook's "What's Different"
    /// opens with "### The Player's Handbook", which is a real subheading and
    /// not a duplicate, and deleting it would remove content.
    /// </para>
    /// </remarks>
    internal static string StripLeadingHeadingMatching(string body, string title)
    {
        var match = LeadingHeading().Match(body);

        if (!match.Success)
        {
            return body;
        }

        var heading = ChapterPrefix().Replace(match.Groups["heading"].Value, "");

        if (!Comparable(heading).Equals(Comparable(title), StringComparison.Ordinal))
        {
            return body;
        }

        return body[match.Length..].TrimStart('\n');
    }

    private static string Comparable(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a stable key matching the <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> pattern
    /// every schema enforces: lower-case, with each run of non-alphanumeric
    /// characters collapsed to a single hyphen. Accented letters are folded to
    /// their base letter first, so a name the archive spells with a diacritic
    /// and one it spells without resolve to the same key.
    /// </summary>
    internal static string Slug(params string?[] parts)
    {
        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            foreach (var character in part.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

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
}
