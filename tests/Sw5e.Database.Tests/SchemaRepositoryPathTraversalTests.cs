using Shouldly;
using Sw5e.Database.Schemas;
using Xunit;

namespace Sw5e.Database.Tests;

/// <summary>
/// Proves that <see cref="SchemaRepository"/>'s path-traversal guard actually
/// refuses to load files outside its schema root.
///
/// The pre-existing traversal tests in <c>SchemaValidatorTests</c> aim at
/// targets that do not exist on disk, so they pass whether or not the guard is
/// present: the "not found" branch swallows them either way. These tests plant
/// a real, loadable schema file outside the root first, then assert it stays
/// unreachable through the root-scoped repository. Delete the body of
/// <c>SchemaRepository.ResolveSchemaPath</c> and these tests fail, which is the
/// whole point of them.
/// </summary>
public sealed class SchemaRepositoryPathTraversalTests : IDisposable
{
    private const string PlantedSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "planted outside the schema root",
      "type": "object"
    }
    """;

    private readonly string _sandbox;
    private readonly string _schemaRoot;

    public SchemaRepositoryPathTraversalTests()
    {
        // A sandbox laid out as:
        //   {sandbox}/schemas/source/v1.json        <- inside the root
        //   {sandbox}/outside-probe/v1.json         <- the planted target
        // The repository is rooted at {sandbox}/schemas, so "outside-probe" is
        // reachable only by escaping that root.
        _sandbox = Path.Combine(
            Path.GetTempPath(),
            $"sw5e-schema-traversal-{Guid.NewGuid():N}");

        _schemaRoot = Path.Combine(_sandbox, "schemas");

        Directory.CreateDirectory(Path.Combine(_schemaRoot, "source"));
        File.WriteAllText(Path.Combine(_schemaRoot, "source", "v1.json"), PlantedSchema);

        Directory.CreateDirectory(Path.Combine(_sandbox, "outside-probe"));
        File.WriteAllText(Path.Combine(_sandbox, "outside-probe", "v1.json"), PlantedSchema);
    }

    /// <summary>
    /// Control: the planted file is genuinely present and genuinely loadable,
    /// so a later failure to load it through the guarded repository is the
    /// guard doing its job and not a missing or malformed target.
    /// </summary>
    [Fact]
    public void PlantedSchemaOutsideRoot_IsLoadableWhenItIsInsideTheRoot()
    {
        var unguarded = new SchemaRepository(_sandbox);

        Should.NotThrow(() => unguarded.Get("outside-probe", 1));
    }

    [Fact]
    public void Get_DoesNotLoadPlantedSchemaViaParentDirectoryTraversal()
    {
        var repository = new SchemaRepository(_schemaRoot);

        Should.Throw<SchemaNotFoundException>(
            () => repository.Get("../outside-probe", 1),
            "a content type that escapes the schema root must never resolve, " +
            "even when the file it points at exists");
    }

    [Fact]
    public void Get_DoesNotLoadPlantedSchemaViaNestedParentDirectoryTraversal()
    {
        var repository = new SchemaRepository(_schemaRoot);

        Should.Throw<SchemaNotFoundException>(
            () => repository.Get("source/../../outside-probe", 1),
            "a content type containing a directory separator must never " +
            "resolve, even when the file it points at exists");
    }

    /// <summary>
    /// An absolute content type escapes the root without containing "..":
    /// Path.Combine discards the root entirely when its second segment is
    /// rooted. This is the case a naive ".." check alone would miss.
    /// </summary>
    [Fact]
    public void Get_DoesNotLoadPlantedSchemaViaAbsolutePath()
    {
        var repository = new SchemaRepository(_schemaRoot);
        var absoluteContentType = Path.Combine(_sandbox, "outside-probe");

        Should.Throw<SchemaNotFoundException>(
            () => repository.Get(absoluteContentType, 1),
            "an absolute content type must never resolve, even when the file " +
            "it points at exists");
    }

    /// <summary>
    /// The guard must not be so blunt that it breaks ordinary resolution.
    /// </summary>
    [Fact]
    public void Get_StillResolvesLegitimateContentTypeInsideRoot()
    {
        var repository = new SchemaRepository(_schemaRoot);

        Should.NotThrow(() => repository.Get("source", 1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }
}
