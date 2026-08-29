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
            var path = Path.Combine(_root, key.Item1, $"v{key.Item2}.json");

            if (!File.Exists(path))
            {
                throw new SchemaNotFoundException(key.Item1, key.Item2);
            }

            return JsonSchema.FromFile(path, _buildOptions);
        });
}
