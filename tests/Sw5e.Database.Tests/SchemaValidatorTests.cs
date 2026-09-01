using System.Text.Json.Nodes;
using Shouldly;
using Sw5e.Database.Schemas;
using Xunit;

namespace Sw5e.Database.Tests;

public sealed class SchemaValidatorTests
{
    private static string SchemaRoot =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas");

    private static SchemaValidator CreateValidator() =>
        new(new SchemaRepository(SchemaRoot));

    [Fact]
    public void ListContentTypes_DiscoversSourceSchema()
    {
        var repository = new SchemaRepository(SchemaRoot);

        repository.ListContentTypes().ShouldContain("source");
    }

    [Fact]
    public void Validate_AcceptsWellFormedSource()
    {
        var document = JsonNode.Parse("""
        {
          "key": "players-handbook",
          "title": "Player's Handbook",
          "abbreviation": "PHB",
          "isOfficial": true
        }
        """)!;

        var result = CreateValidator().Validate("source", 1, document);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RejectsSourceMissingRequiredField()
    {
        var document = JsonNode.Parse("""
        { "key": "players-handbook", "title": "Player's Handbook" }
        """)!;

        var result = CreateValidator().Validate("source", 1, document);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Contains("required"));
    }

    [Fact]
    public void Validate_RejectsSourceWithInvalidKeyPattern()
    {
        var document = JsonNode.Parse("""
        {
          "key": "Players Handbook",
          "title": "Player's Handbook",
          "abbreviation": "PHB",
          "isOfficial": true
        }
        """)!;

        var result = CreateValidator().Validate("source", 1, document);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Contains("pattern"));
    }

    [Fact]
    public void Validate_RejectsUnknownProperties()
    {
        var document = JsonNode.Parse("""
        {
          "key": "players-handbook",
          "title": "Player's Handbook",
          "abbreviation": "PHB",
          "isOfficial": true,
          "unexpectedField": "should be rejected"
        }
        """)!;

        var result = CreateValidator().Validate("source", 1, document);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Contains("additionalProperties"));
    }

    [Fact]
    public void Get_ThrowsForUnknownContentType()
    {
        var repository = new SchemaRepository(SchemaRoot);

        Should.Throw<SchemaNotFoundException>(() => repository.Get("does-not-exist", 1));
    }

    [Fact]
    public void Get_ThrowsForContentTypeContainingParentDirectoryTraversal()
    {
        var repository = new SchemaRepository(SchemaRoot);

        Should.Throw<SchemaNotFoundException>(() => repository.Get("../../../../etc", 1));
    }

    [Fact]
    public void Get_ThrowsForContentTypeContainingDirectorySeparator()
    {
        var repository = new SchemaRepository(SchemaRoot);

        Should.Throw<SchemaNotFoundException>(() => repository.Get("source/../../secrets", 1));
    }

    [Fact]
    public void Get_StillResolvesLegitimateContentTypeAfterTraversalGuards()
    {
        var repository = new SchemaRepository(SchemaRoot);

        Should.NotThrow(() => repository.Get("source", 1));
    }

    /// <summary>
    /// A document that breaks the schema in two places is reported as two
    /// failures, not one and not none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins a contract rather than a behaviour anybody asked for directly.
    /// <c>sw5e-api</c> consumes this validator as a submodule and returns its
    /// error list to whoever is authoring the document, so an editor can put
    /// each failure beside the field that caused it. That only works while
    /// evaluation stays at <c>OutputFormat.List</c>: under
    /// <c>OutputFormat.Flag</c> the same document is still correctly rejected,
    /// every existing test here still passes, and the API silently starts
    /// telling contributors "this is wrong" with nowhere to point.
    /// </para>
    /// <para>
    /// Both violations are the ordinary kinds. <c>description</c> is required
    /// and missing; <c>quantumEntanglement</c> is refused because every schema
    /// in this repository sets <c>additionalProperties: false</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Validate_ReportsEveryFailureAndNotJustTheFirst()
    {
        var document = JsonNode.Parse("""
        {
          "key": "malformed",
          "name": "Malformed",
          "contentSet": "core",
          "quantumEntanglement": true
        }
        """)!;

        var result = CreateValidator().Validate("armor-property", 1, document);

        result.IsValid.ShouldBeFalse();

        result.Errors.Count.ShouldBeGreaterThanOrEqualTo(
            2,
            "The API reports these to whoever is authoring the document, one per " +
            "field. Collapsing them to a single failure leaves an editor with " +
            "nothing to point at.");
    }
}
