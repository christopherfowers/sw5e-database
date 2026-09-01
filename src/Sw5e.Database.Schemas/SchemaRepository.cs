using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;

namespace Sw5e.Database.Schemas;

/// <summary>
/// Loads versioned JSON Schema documents from disk. Schema files live at
/// <c>{root}/{contentType}/v{version}.json</c> and are the authoritative
/// definition of every content type's payload.
/// </summary>
public sealed class SchemaRepository
{
    /// <summary>
    /// The version assumed for a content type whose directory cannot be read.
    /// </summary>
    /// <remarks>
    /// Every schema in this repository is at v1 today. This is what
    /// <see cref="LatestVersion"/> falls back to, not a hard-coded answer: the
    /// probe reads the directory, so publishing <c>v2.json</c> is picked up
    /// without a code change, which is the property the design asks for — a
    /// content type's definition is a reviewed schema file, never a release.
    /// </remarks>
    public const int FallbackVersion = 1;

    private readonly string _root;
    private readonly ConcurrentDictionary<(string, int), JsonSchema> _cache = new();
    private readonly ConcurrentDictionary<(string, int), JsonDocument> _documents = new();
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);

    // Schemas are built against a registry scoped to this repository instance
    // rather than the process-wide global registry, so that separate
    // SchemaRepository instances (e.g. one per test, or one per hosted
    // request) never collide over the same schema $id.
    private readonly BuildOptions _buildOptions = new() { SchemaRegistry = new SchemaRegistry() };

    public SchemaRepository(string schemaRootPath)
    {
        _root = Path.GetFullPath(schemaRootPath);

        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Schema root not found: {_root}");
        }
    }

    public IReadOnlyList<string> ListContentTypes() =>
        Directory.EnumerateDirectories(_root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();

    public JsonSchema Get(string contentType, int version) =>
        _cache.GetOrAdd((contentType, version), key =>
        {
            var (type, ver) = key;
            var path = ResolveSchemaPath(type, ver);

            if (path is null || !File.Exists(path))
            {
                // A path that escapes the schema root and a content type that
                // legitimately does not exist must be indistinguishable to
                // the caller, so both fall through to the same exception.
                throw new SchemaNotFoundException(type, ver);
            }

            return JsonSchema.FromFile(path, _buildOptions);
        });

    /// <summary>
    /// The schema as it is written, rather than as it is compiled.
    /// </summary>
    /// <remarks>
    /// <see cref="Get"/> returns a validator, and a validator has no reason to
    /// remember the order its keywords were declared in. The canonical file
    /// format does: a content document's members are written in the order its
    /// schema declares them, so the one thing a compiled schema deliberately
    /// discards is the thing <see cref="CanonicalContent"/> needs. Reading the
    /// document a second time is cheaper than making the compiled form carry
    /// ordering it does not otherwise use, and it keeps the two concerns from
    /// depending on an implementation detail of the schema library.
    /// </remarks>
    /// <exception cref="SchemaNotFoundException">
    /// No schema is published for this content type at this version.
    /// </exception>
    public JsonElement GetDocument(string contentType, int version) =>
        _documents.GetOrAdd((contentType, version), key =>
        {
            var (type, ver) = key;
            var path = ResolveSchemaPath(type, ver);

            if (path is null || !File.Exists(path))
            {
                throw new SchemaNotFoundException(type, ver);
            }

            return JsonDocument.Parse(File.ReadAllBytes(path));
        }).RootElement;

    /// <summary>The highest <c>v{n}.json</c> published for a content type.</summary>
    /// <remarks>
    /// A directory probe rather than a constant, so adding <c>v2.json</c> to a
    /// type is a content change and not a code change. A type with no directory
    /// at all reports <see cref="FallbackVersion"/>; asking for its schema then
    /// fails with <see cref="SchemaNotFoundException"/>, which is the honest
    /// answer and the one every caller already handles.
    /// </remarks>
    public int LatestVersion(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        return _versions.GetOrAdd(contentType, type =>
        {
            var directory = ResolveTypeDirectory(type);

            if (directory is null || !Directory.Exists(directory))
            {
                return FallbackVersion;
            }

            var highest = 0;

            foreach (var file in Directory.EnumerateFiles(directory, "v*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                if (name.Length > 1 &&
                    int.TryParse(name.AsSpan(1), out var version) &&
                    version > highest)
                {
                    highest = version;
                }
            }

            return highest == 0 ? FallbackVersion : highest;
        });
    }

    /// <summary>
    /// Resolves <c>{root}/{contentType}/v{version}.json</c> and confirms the
    /// result is genuinely inside <c>_root</c>, refusing to follow path
    /// traversal (e.g. <c>contentType = "../../etc"</c>) or a <c>contentType</c>
    /// containing a directory separator out to the filesystem. Returns null
    /// for any input that fails these checks.
    /// </summary>
    private string? ResolveSchemaPath(string contentType, int version)
    {
        var directory = ResolveTypeDirectory(contentType);

        return directory is null ? null : Path.Combine(directory, $"v{version}.json");
    }

    /// <summary>
    /// Resolves <c>{root}/{contentType}</c> and confirms the result is
    /// genuinely inside <c>_root</c>. Returns null for any input that is not.
    /// </summary>
    private string? ResolveTypeDirectory(string contentType)
    {
        // No legitimate content type name contains a directory separator or
        // a "..", on any OS, so reject those outright before touching the
        // filesystem at all.
        if (string.IsNullOrEmpty(contentType) ||
            contentType.Contains("..", StringComparison.Ordinal) ||
            contentType.IndexOf('/') >= 0 ||
            contentType.IndexOf('\\') >= 0)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, contentType));
        var relative = Path.GetRelativePath(_root, candidate);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return candidate;
    }
}
