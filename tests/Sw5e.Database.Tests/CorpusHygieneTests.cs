using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// The faults a schema cannot see.
/// </summary>
/// <remarks>
/// <para>
/// Every document here already validates against its schema and resolves its
/// cross-references — <see cref="SeedContentTests"/> proves that. None of that
/// says anything about the <em>text</em>. A name with two spaces in it, a
/// description that is the empty string, a paragraph whose line breaks were
/// eaten on the way out of the original site: all of those are a valid string
/// where a string was required, and all of them reach a reader.
/// </para>
/// <para>
/// These run over the whole corpus at once rather than per type, because the
/// faults are not properties of a type. They arrived with an import, and the
/// next import will bring the next batch.
/// </para>
/// <para>
/// The list is deliberately short and every entry is something that is always
/// wrong. There is no check here for prose that reads badly or a stat that is
/// implausible; those need a person, and a test that guesses at them would
/// spend its life being suppressed.
/// </para>
/// </remarks>
public sealed class CorpusHygieneTests
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    /// <summary>
    /// A markdown heading marker with a word character pressed against it.
    /// </summary>
    /// <remarks>
    /// The signature of line breaks having been stripped from a body of prose,
    /// which is how 271 creature descriptions once arrived reading
    /// "…indicated.#### Lair Actions". Markdown renders that as one paragraph
    /// with a row of hashes in the middle of it, so it is visible to a reader
    /// and invisible to a schema.
    /// </remarks>
    private static readonly Regex WeldedHeading =
        new(@"[A-Za-z0-9.,;:)""']#{1,6}\s", RegexOptions.Compiled);

    /// <summary>Every string in every document, with the path that reached it.</summary>
    private static IEnumerable<(string File, string Path, string Value)> AllStrings()
    {
        foreach (var file in Directory.EnumerateFiles(ContentRoot, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));

            foreach (var found in Walk(node, ""))
            {
                yield return (Path.GetRelativePath(ContentRoot, file), found.Path, found.Value);
            }
        }
    }

    private static IEnumerable<(string Path, string Value)> Walk(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    foreach (var found in Walk(value, $"{path}.{key}")) yield return found;
                }
                break;

            case JsonArray a:
                for (var index = 0; index < a.Count; index += 1)
                {
                    foreach (var found in Walk(a[index], $"{path}[{index}]")) yield return found;
                }
                break;

            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                yield return (path, v.GetValue<string>());
                break;
        }
    }

    /// <summary>
    /// Formats the offenders for the assertion message.
    /// </summary>
    /// <remarks>
    /// Capped, because a fault introduced by an import is a fault in thousands
    /// of documents at once and a failure message listing all of them is a
    /// failure message nobody reads to the end of.
    /// </remarks>
    private static string Describe(IReadOnlyList<(string File, string Path, string Value)> found)
    {
        var shown = found.Take(10).Select(item =>
            $"  {item.File}{item.Path}: {Snippet(item.Value)}");

        var more = found.Count > 10 ? $"{Environment.NewLine}  ...and {found.Count - 10} more" : "";

        return Environment.NewLine + string.Join(Environment.NewLine, shown) + more;
    }

    private static string Snippet(string value) =>
        value.Length <= 80 ? value : value[..80] + "...";

    [Fact]
    public void NoStringIsBlankOrPadded()
    {
        // An empty string is a field somebody meant to fill in; a padded one
        // renders as a gap the reader cannot account for. Both validate.
        var offenders = AllStrings()
            .Where(item => item.Value.Length > 0 && item.Value.Trim() != item.Value)
            .ToList();

        offenders.ShouldBeEmpty($"padded strings: {Describe(offenders)}");

        var blanks = AllStrings().Where(item => item.Value.Length == 0).ToList();

        blanks.ShouldBeEmpty($"empty strings: {Describe(blanks)}");
    }

    [Fact]
    public void NoNameCarriesADoubleSpace()
    {
        // Found one: "Dexterity Augment  (Champion)". It is the kind of thing
        // nobody sees until it is between two other entries in a sorted list.
        var offenders = AllStrings()
            .Where(item => item.Path.EndsWith(".name", StringComparison.Ordinal))
            .Where(item => item.Value.Contains("  ", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty($"names with a double space: {Describe(offenders)}");
    }

    [Fact]
    public void NoProseHasHadItsLineBreaksEaten()
    {
        var offenders = AllStrings()
            .Where(item => item.Value.Length > 60 && WeldedHeading.IsMatch(item.Value))
            .ToList();

        offenders.ShouldBeEmpty(
            "a markdown heading is welded to the sentence before it, which means the " +
            $"line breaks were lost on import: {Describe(offenders)}");
    }

    [Fact]
    public void NoStringCarriesAnEscapeThatWasNeverUnescaped()
    {
        // A backslash-n that survived into the value renders as two characters
        // in the middle of a sentence. It means something wrote a string that
        // had already been JSON-encoded once.
        var offenders = AllStrings()
            .Where(item => item.Value.Contains(@"\n", StringComparison.Ordinal)
                || item.Value.Contains(@"\t", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty($"doubly-encoded escapes: {Describe(offenders)}");
    }

    [Fact]
    public void NoLinkIsInsecure()
    {
        var offenders = AllStrings()
            .Where(item => item.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.ShouldBeEmpty($"plain-http links: {Describe(offenders)}");
    }

    [Fact]
    public void TheScanActuallyReachesTheCorpus()
    {
        // The control. Every assertion above passes trivially against an empty
        // sequence, so a change that stopped this walking the tree — a renamed
        // directory, a parser returning null — would look like a clean corpus.
        var strings = AllStrings().ToList();

        strings.Count.ShouldBeGreaterThan(50_000);
        strings.ShouldContain(item => item.Path.EndsWith(".name", StringComparison.Ordinal));
    }
}
