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
}
