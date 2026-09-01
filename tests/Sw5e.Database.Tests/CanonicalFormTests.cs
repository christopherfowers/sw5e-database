using System.Text;
using System.Text.Json;
using Shouldly;
using Sw5e.Database.Schemas;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Every committed content file is in canonical form, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// This repository is no longer the only place content is written. The site's
/// authoring endpoints write into PostgreSQL, and the API's exporter turns what
/// is published there back into this tree so that a change made on the site
/// arrives here as a reviewable pull request. That export renders each document
/// with <see cref="CanonicalContent"/>, because <c>jsonb</c> keeps a document's
/// values and discards its text: member order, indentation and whitespace are
/// gone by the time the exporter reads a row.
/// </para>
/// <para>
/// Which makes this the load-bearing assertion for the whole arrangement. If a
/// committed file is not already what the canonical writer would produce, the
/// first export rewrites it — and the reviewer of that pull request is shown a
/// diff of reformatting with the actual edit hidden somewhere inside it. Every
/// export after that repeats the argument.
/// </para>
/// <para>
/// It is also what pins the schemas. Member order is taken from the order a
/// schema declares its <c>properties</c>, so reordering a schema is a change to
/// the file format of every document of that type. That is a defensible thing
/// to do deliberately and a terrible thing to do by accident, and this test is
/// the difference: reorder a schema without reordering the content and the
/// build goes red here, naming the files.
/// </para>
/// </remarks>
public sealed class CanonicalFormTests
{
    private static readonly string ContentRoot =
        Path.Combine(LegacyArchive.RepositoryRoot, "content");

    private static readonly CanonicalContent Canonical =
        new(new SchemaRepository(LegacyArchive.SchemaRoot));

    [Fact]
    public void EveryCommittedDocumentIsExactlyWhatTheCanonicalWriterProduces()
    {
        var failures = new List<string>();
        var checkedCount = 0;

        foreach (var (contentType, file) in Documents())
        {
            // Read as text with the line endings normalised: a checkout on
            // Windows may hold CRLF, and which of the two sits in a working
            // tree is git's decision rather than the format's. Everything else
            // — member order, indentation, escaping, the trailing newline — is
            // compared exactly.
            var committed = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n");

            using var document = JsonDocument.Parse(committed);

            var rendered = Canonical.Render(contentType, document.RootElement);

            checkedCount++;

            if (!string.Equals(rendered, committed, StringComparison.Ordinal))
            {
                failures.Add($"{contentType}/{Path.GetFileName(file)}: {Describe(committed, rendered)}");
            }
        }

        checkedCount.ShouldBeGreaterThan(0, $"No content was found under '{ContentRoot}'.");

        failures.ShouldBeEmpty(
            "These documents are not in canonical form, so the next export would rewrite them. " +
            "Run 'dotnet run --project src/Sw5e.Database.Tools -- canonicalise' and review the diff:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures.Take(20)) +
            (failures.Count > 20
                ? $"{Environment.NewLine}  ... and {failures.Count - 20} more."
                : string.Empty));
    }

    /// <summary>
    /// Rendering a rendered document changes nothing.
    /// </summary>
    /// <remarks>
    /// The test above proves the corpus matches the writer. This proves the
    /// writer has a fixed point at all — that it is not, say, appending a
    /// newline or re-escaping a character on every pass. Without it a writer
    /// that drifted a little each time would be caught only once the corpus had
    /// already drifted with it.
    /// </remarks>
    [Fact]
    public void RenderingIsIdempotent()
    {
        var failures = new List<string>();

        foreach (var (contentType, file) in Documents())
        {
            using var first = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));

            var once = Canonical.Render(contentType, first.RootElement);

            using var second = JsonDocument.Parse(once);

            var twice = Canonical.Render(contentType, second.RootElement);

            if (!string.Equals(once, twice, StringComparison.Ordinal))
            {
                failures.Add($"{contentType}/{Path.GetFileName(file)}");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A document whose members arrive in a different order renders the same.
    /// </summary>
    /// <remarks>
    /// This is the property the exporter depends on and the one the corpus
    /// cannot demonstrate on its own: the committed files are already in
    /// canonical order, so a writer that simply copied the order it was given
    /// would pass every assertion above. Reversing the members first is what
    /// separates "orders the document" from "preserves the order it was
    /// handed", and reversal rather than a shuffle so a failure reproduces.
    /// </remarks>
    [Fact]
    public void MemberOrderIsImposedRatherThanPreserved()
    {
        var compared = 0;

        foreach (var (contentType, file) in Documents())
        {
            var committed = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n");

            using var document = JsonDocument.Parse(committed);
            using var reversed = JsonDocument.Parse(Reverse(document.RootElement));

            Canonical.Render(contentType, reversed.RootElement)
                     .ShouldBe(committed, $"{contentType}/{Path.GetFileName(file)}");

            compared++;
        }

        compared.ShouldBeGreaterThan(0);
    }

    /// <summary>The same document with every object's members reversed.</summary>
    private static string Reverse(JsonElement element)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(element, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (var member in value.EnumerateObject().Reverse())
                    {
                        writer.WritePropertyName(member.Name);
                        Write(member.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    foreach (var item in value.EnumerateArray())
                    {
                        Write(item, writer);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }

    /// <summary>Where the first difference is, in terms a reviewer can act on.</summary>
    private static string Describe(string committed, string rendered)
    {
        var shared = 0;

        while (shared < committed.Length &&
               shared < rendered.Length &&
               committed[shared] == rendered[shared])
        {
            shared++;
        }

        var line = committed.Take(shared).Count(character => character == '\n') + 1;

        return $"first differs at line {line}: committed {Excerpt(committed, shared)}, " +
               $"canonical {Excerpt(rendered, shared)}";
    }

    private static string Excerpt(string text, int from) =>
        from >= text.Length
            ? "(end of file)"
            : $"\"{text.Substring(from, Math.Min(40, text.Length - from)).Replace("\n", "\\n")}\"";

    /// <summary>Every committed content document, with the type it is filed under.</summary>
    private static IEnumerable<(string ContentType, string File)> Documents()
    {
        if (!Directory.Exists(ContentRoot))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(ContentRoot).Order(StringComparer.Ordinal))
        {
            var contentType = Path.GetFileName(directory);

            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                yield return (contentType, file);
            }
        }
    }
}
