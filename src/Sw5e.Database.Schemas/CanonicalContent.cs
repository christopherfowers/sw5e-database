using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sw5e.Database.Schemas;

/// <summary>
/// Writes a content document in the one form this repository commits it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a canonical form is needed at all.</b> Content is authored in two
/// places now. Most of it arrives as a file in a pull request; the rest arrives
/// through the site's authoring endpoints and lives in PostgreSQL, which stores
/// documents as <c>jsonb</c> — a form that keeps the values and throws away the
/// text: member order, indentation and whitespace are all gone by the time a
/// document is read back. Exporting the database into this repository therefore
/// cannot reproduce a file by remembering how it was written. It has to derive
/// the bytes from the document, and every writer that derives bytes from a
/// document has to agree, exactly, or every export produces a diff of
/// reformatting noise with the actual change buried in it.
/// </para>
/// <para>
/// <b>Where the member order comes from.</b> From the type's schema, in the
/// order the schema declares its <c>properties</c>. The schema is already the
/// definition of the type and already reviewed on the way in, so it is the one
/// place a field order can be stated without inventing a second registry that
/// could disagree with it. The rule is total: members the schema does not
/// declare — which <c>additionalProperties: false</c> makes impossible today —
/// are written after the declared ones in ordinal order, so an undeclared
/// member cannot make the output depend on the order it happened to arrive in.
/// </para>
/// <para>
/// <b>Everything else about the format</b> is what the corpus was already
/// written with: two-space indentation, a bare newline, a trailing newline,
/// UTF-8 without a byte-order mark, and the relaxed encoder so an apostrophe
/// stays an apostrophe and an em dash stays an em dash instead of becoming six
/// characters of escape that no reviewer can read.
/// </para>
/// <para>
/// <b>Numbers are copied, not reformatted.</b> The archive contains values such
/// as <c>0.0</c> and <c>0.3333333333333333</c>, and a writer that parsed those
/// into a double and printed it again would be free to return <c>0</c> and
/// <c>0.33333333333333331</c>. The raw text is what is written out, which is
/// also what PostgreSQL's <c>numeric</c> preserves, so a document survives the
/// trip through the database unchanged.
/// </para>
/// </remarks>
public sealed class CanonicalContent(SchemaRepository schemas)
{
    /// <summary>How deep a chain of <c>$ref</c>s or compositions is followed.</summary>
    /// <remarks>
    /// A schema is allowed to be recursive, and a document is not required to
    /// stop it. The cap is what keeps a malformed or circular schema from
    /// turning a formatting decision into a stack overflow; reaching it costs
    /// nothing worse than a member order falling back to ordinal.
    /// </remarks>
    private const int MaximumSchemaDepth = 16;

    /// <summary>Keywords whose subschemas contribute members to the same object.</summary>
    private static readonly string[] CompositionKeywords = ["allOf", "anyOf", "oneOf"];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,

        // Pinned rather than left to the platform, so a run on Windows and a
        // run on the CI runner produce the same bytes. What a working tree
        // holds after that is git's business.
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // The writer is fed an already-parsed document, so structural
        // validation would re-prove what the parser proved.
        SkipValidation = true,
    };

    private readonly SchemaRepository _schemas =
        schemas ?? throw new ArgumentNullException(nameof(schemas));

    /// <summary>UTF-8 with no byte-order mark: what a content file is written as.</summary>
    public static UTF8Encoding FileEncoding { get; } = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Renders a document exactly as its file is committed, trailing newline
    /// included.
    /// </summary>
    /// <param name="contentType">The type key, which is also its directory name.</param>
    /// <param name="document">The document. Must be a JSON object.</param>
    /// <exception cref="SchemaNotFoundException">
    /// No schema is published for this content type.
    /// </exception>
    public string Render(string contentType, JsonElement document) =>
        Render(contentType, _schemas.LatestVersion(contentType), document);

    /// <inheritdoc cref="Render(string, JsonElement)"/>
    /// <param name="contentType">The type key, which is also its directory name.</param>
    /// <param name="version">Which published version of the schema to order by.</param>
    /// <param name="document">The document. Must be a JSON object.</param>
    public string Render(string contentType, int version, JsonElement document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "A content document is a JSON object.", nameof(document));
        }

        return Render(_schemas.GetDocument(contentType, version), document);
    }

    /// <summary>Renders a document against a schema already in hand.</summary>
    /// <param name="schema">The schema document, as written.</param>
    /// <param name="document">The document. Must be a JSON object.</param>
    public static string Render(JsonElement schema, JsonElement document)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(document, schema, schema, writer, 0);
        }

        // The trailing newline every text file in this repository ends with. It
        // is appended rather than written, because the writer's business is the
        // JSON value and a JSON value does not end in a newline.
        buffer.Write("\n"u8);

        return FileEncoding.GetString(buffer.ToArray());
    }

    private static void Write(
        JsonElement value,
        JsonElement? schema,
        JsonElement root,
        Utf8JsonWriter writer,
        int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var member in Ordered(value, schema, root, depth))
                {
                    writer.WritePropertyName(member.Name);
                    Write(member.Value, Child(schema, root, member.Name, depth), root, writer, depth + 1);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                var item = Items(schema, root, depth);

                foreach (var element in value.EnumerateArray())
                {
                    Write(element, item, root, writer, depth + 1);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                // Re-encoded rather than copied, so a document that arrived
                // with "—" and one that arrived with an em dash are
                // written the same way. The two are the same document, and a
                // file format in which they are not is a file format that
                // diffs when nothing changed.
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(value.GetBoolean());
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// One object's members, in the order its schema declares them.
    /// </summary>
    /// <remarks>
    /// Ordered by a stable sort over the members that are actually present,
    /// rather than by looking up each declared name in turn. A document with a
    /// member repeated — which a parser permits and this writer has no business
    /// silently repairing — keeps both copies rather than losing one.
    /// </remarks>
    private static IEnumerable<JsonProperty> Ordered(
        JsonElement value,
        JsonElement? schema,
        JsonElement root,
        int depth)
    {
        var declared = Declared(schema, root, depth);

        var rank = new Dictionary<string, int>(declared.Count, StringComparer.Ordinal);

        for (var index = 0; index < declared.Count; index++)
        {
            rank.TryAdd(declared[index], index);
        }

        return value.EnumerateObject()
            .OrderBy(member => rank.TryGetValue(member.Name, out var index) ? index : declared.Count)
            .ThenBy(member => rank.ContainsKey(member.Name) ? string.Empty : member.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every property name the schema declares for this position, in order.
    /// </summary>
    private static List<string> Declared(JsonElement? schema, JsonElement root, int depth)
    {
        var order = new List<string>();

        foreach (var node in Compose(schema, root, depth))
        {
            if (!node.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!order.Contains(property.Name, StringComparer.Ordinal))
                {
                    order.Add(property.Name);
                }
            }
        }

        return order;
    }

    /// <summary>The schema for one member, or null when none applies.</summary>
    private static JsonElement? Child(JsonElement? schema, JsonElement root, string name, int depth)
    {
        foreach (var node in Compose(schema, root, depth))
        {
            if (node.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(name, out var child))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>The schema for an array's elements, or null when none applies.</summary>
    private static JsonElement? Items(JsonElement? schema, JsonElement root, int depth)
    {
        foreach (var node in Compose(schema, root, depth))
        {
            if (node.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Object)
            {
                return items;
            }
        }

        return null;
    }

    /// <summary>
    /// The schema itself plus every subschema composed into it.
    /// </summary>
    /// <remarks>
    /// <c>allOf</c>, <c>anyOf</c> and <c>oneOf</c> all contribute properties to
    /// the same object, so all three are walked. The node's own
    /// <c>properties</c> come first, which is what makes the composed branches
    /// a tie-break rather than a competing declaration: in this repository the
    /// branches carry constraints on members the parent has already declared,
    /// and the parent is where the order is written down.
    /// </remarks>
    private static IEnumerable<JsonElement> Compose(JsonElement? schema, JsonElement root, int depth)
    {
        if (schema is not { } node || depth > MaximumSchemaDepth)
        {
            yield break;
        }

        if (Dereference(node, root) is not { } resolved ||
            resolved.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return resolved;

        foreach (var keyword in CompositionKeywords)
        {
            if (!resolved.TryGetProperty(keyword, out var branches) ||
                branches.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var branch in branches.EnumerateArray())
            {
                foreach (var composed in Compose(branch, root, depth + 1))
                {
                    yield return composed;
                }
            }
        }
    }

    /// <summary>
    /// Follows a local <c>$ref</c> to what it points at.
    /// </summary>
    /// <remarks>
    /// Only same-document pointers are followed. A schema in this repository
    /// that referenced another file would be resolvable, but the resolution
    /// would have to be identical to the validator's, and the two agreeing by
    /// coincidence is exactly the kind of drift the shared library exists to
    /// avoid. An unfollowable reference is not an error here: the member order
    /// falls back to ordinal, and the validator is what rejects the document.
    /// </remarks>
    private static JsonElement? Dereference(JsonElement node, JsonElement root)
    {
        for (var hop = 0; hop <= MaximumSchemaDepth; hop++)
        {
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("$ref", out var reference) ||
                reference.ValueKind != JsonValueKind.String)
            {
                return node;
            }

            var pointer = reference.GetString();

            if (pointer is null || !pointer.StartsWith('#'))
            {
                return null;
            }

            if (!TryFollow(root, pointer[1..], out node))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Walks a JSON Pointer from the schema's root.</summary>
    private static bool TryFollow(JsonElement root, string pointer, out JsonElement target)
    {
        target = root;

        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            return false;
        }

        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal)
                           .Replace("~0", "~", StringComparison.Ordinal);

            switch (target.ValueKind)
            {
                case JsonValueKind.Object when target.TryGetProperty(token, out var member):
                    target = member;
                    break;

                case JsonValueKind.Array when int.TryParse(token, out var index) &&
                                              index >= 0 &&
                                              index < target.GetArrayLength():
                    target = target[index];
                    break;

                default:
                    return false;
            }
        }

        return true;
    }
}
