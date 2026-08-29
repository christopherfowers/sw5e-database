using System.Collections.Concurrent;
using Json.Schema;

namespace Sw5e.Database.Schemas;

/// <summary>
/// Loads versioned JSON Schema documents from disk. Schema files live at
/// <c>{root}/{contentType}/v{version}.json</c> and are the authoritative
/// definition of every content type's payload.
/// </summary>
public sealed class SchemaRepository
{
    private readonly string _root;
    private readonly ConcurrentDictionary<(string, int), JsonSchema> _cache = new();

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
    /// Resolves <c>{root}/{contentType}/v{version}.json</c> and confirms the
    /// result is genuinely inside <c>_root</c>, refusing to follow path
    /// traversal (e.g. <c>contentType = "../../etc"</c>) or a <c>contentType</c>
    /// containing a directory separator out to the filesystem. Returns null
    /// for any input that fails these checks.
    /// </summary>
    private string? ResolveSchemaPath(string contentType, int version)
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

        var candidate = Path.GetFullPath(Path.Combine(_root, contentType, $"v{version}.json"));
        var relative = Path.GetRelativePath(_root, candidate);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return candidate;
    }
}
