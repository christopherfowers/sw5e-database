using System.Text.Json.Nodes;
using FluentAssertions;
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

        repository.ListContentTypes().Should().Contain("source");
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

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsSourceMissingRequiredField()
    {
        var document = JsonNode.Parse("""
        { "key": "players-handbook", "title": "Player's Handbook" }
        """)!;

        var result = CreateValidator().Validate("source", 1, document);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
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

        result.IsValid.Should().BeFalse();
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

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Get_ThrowsForUnknownContentType()
    {
        var repository = new SchemaRepository(SchemaRoot);

        var act = () => repository.Get("does-not-exist", 1);

        act.Should().Throw<SchemaNotFoundException>();
    }
}
