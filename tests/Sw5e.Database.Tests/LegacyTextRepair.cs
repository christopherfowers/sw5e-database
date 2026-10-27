using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sw5e.Database.Tests;

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
/// <b>These rules also exist in JavaScript</b>, as
/// <c>scripts/lib/repair-text.mjs</c> in the sw5e-web repository, which is
/// where they were first written and which this is a port of. That is not
/// accidental duplication and it is not a shared library waiting to be
/// extracted: the two run at different points on different inputs. The
/// JavaScript one repairs the archive as the site builds a dataset straight
/// from it, and this one repairs the archive once, on the way into
/// <c>content/</c>, so that the canonical set is already clean and nothing
/// downstream ever has to know the corruption existed. Content here is meant to
/// be readable and editable by hand; a repair pass that ran over it on every
/// read would mean the file on disk and the document served were different
/// documents. The rules are kept in step by hand, and both sides carry a note
/// saying so.
/// </para>
/// <para>
/// <see cref="LegacyContentMapper"/> deliberately does none of this. Mapping is
/// mechanical and lossless — rename, regroup, drop storage artefacts — and is
/// held to the whole corpus by <see cref="ArchiveConformanceTests"/>. Repair is
/// a judgement call about characters that are gone, and it belongs to the
/// import stage, where each judgement can be named.
/// </para>
/// </remarks>
public static partial class LegacyTextRepair
{
    public const char ReplacementCharacter = '�';

    /// <summary>
    /// Contraction and possessive suffixes. A replacement character wedged
    /// between a letter and one of these, with a word boundary after it, can
    /// only have been an apostrophe: <c>can?t</c>, <c>the tank?s effects</c>,
    /// <c>you?re</c>. The trailing word boundary is what makes this safe: it
    /// stops the rule firing on a dash that happens to precede a word starting
    /// with the same letter, as in <c>strength?something</c>.
    /// </summary>
    [GeneratedRegex(@"(?<=\p{L})�(?=(?:t|s|d|m|re|ve|ll)\b)")]
    private static partial Regex Contraction();

    /// <summary>
    /// A balanced pair of replacement characters, opening after whitespace and
    /// closing before whitespace, punctuation or a table-cell boundary, with
    /// non-space content between them: <c>a ?tell? that</c>, <c>the proverb
    /// ?unity is strength?.</c> Only a quotation mark pairs like that. The
    /// length cap keeps the pairing local, so two unrelated dashes far apart
    /// cannot be mistaken for a quotation.
    /// </summary>
    [GeneratedRegex(@"(^|[\s([])�(?=\S)([^�\n]{0,80}?)(?<=\S)�(?=[\s.,;:!?)\]|]|$)",
        RegexOptions.Multiline)]
    private static partial Regex QuotePair();

    /// <summary>
    /// A replacement character that is the entire content of a markdown table
    /// cell. Only an em dash sets a cell to "none" in these tables. The cell
    /// boundaries are matched as lookaround rather than consumed, so a run of
    /// empty cells repairs every one of them rather than every other one.
    /// </summary>
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
    /// The left-hand guard is what keeps this off proper nouns: two non-space
    /// characters have to precede the dash, which excludes <c>L?vern</c>,
    /// <c>H?sk</c>, <c>F?ress</c> and <c>J?nsone</c>, where the lost letter
    /// sits second in a name that follows a space. Asterisks are in the class
    /// because the corpus welds dashes to markdown emphasis —
    /// <c>**insignia**?the</c> — and a digit is allowed on the right for
    /// <c>the final three scores?12, 10, and 8?to Charisma</c>. That same
    /// sentence is why a digit needs no second character before it: in
    /// <c>and 8?to Charisma</c> the dash follows a space and a single <c>8</c>,
    /// and a digit after a space is never the second letter of a name. A lone letter
    /// on the right is still excluded, which keeps the rule away from
    /// <c>Seelv?n</c> and <c>Ty?k</c>; those two are repaired by <see
    /// cref="NamesFromSource"/> before this rule ever sees them.
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

    [GeneratedRegex(@"\r\n?")]
    private static partial Regex LineEndings();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun();

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

        return text;
    }

    /// <summary>
    /// Repairs a value and normalises its whitespace. Returns null for a value
    /// that is empty, or whose content is entirely replacement characters, so
    /// callers can drop the field rather than write an empty shell.
    /// </summary>
    public static string? Repair(string? value)
    {
        if (value is null || IsTotalLoss(value))
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
    /// nothing but replacement characters and whitespace. A class level table
    /// is full of these — a column that says nothing at this level is printed
    /// as an em dash — and the honest result is no cell rather than a cell
    /// holding a broken glyph.
    /// </summary>
    public static bool IsTotalLoss(string value) =>
        value.Contains(ReplacementCharacter, StringComparison.Ordinal) &&
        value.All(character => character == ReplacementCharacter || char.IsWhiteSpace(character));

    /// <summary>True when repair left at least one unrecoverable character behind.</summary>
    public static bool ContainsUnrepairedLoss(string? value) =>
        value is not null && value.Contains(ReplacementCharacter, StringComparison.Ordinal);

    /// <summary>
    /// Repairs every string in a document, dropping the ones that repair to
    /// nothing along with any container they empty.
    /// </summary>
    /// <remarks>
    /// Property names are left alone. They are field names this repository
    /// chose, not scraped text, and the mapper is the only thing that writes
    /// them.
    /// </remarks>
    public static JsonNode? RepairDocument(JsonNode? node)
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
                    var repaired = RepairDocument(property.Value?.DeepClone());

                    if (repaired is not null)
                    {
                        result[property.Key] = repaired;
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonArray source:
            {
                var result = new JsonArray();

                foreach (var element in source.ToList())
                {
                    var repaired = RepairDocument(element?.DeepClone());

                    if (repaired is not null)
                    {
                        result.Add(repaired);
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
            {
                var repaired = Repair(text);

                return repaired is null ? null : JsonValue.Create(repaired);
            }

            default:
                return node.DeepClone();
        }
    }
}
