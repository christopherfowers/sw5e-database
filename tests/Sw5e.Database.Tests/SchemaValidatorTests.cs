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

/// <summary>
/// The structured half of a refusal.
/// </summary>
/// <remarks>
/// <para>
/// A refusal has always carried three facts — where the failure was, which
/// keyword rejected it, and what the validator wanted to say — and it used to
/// throw two of them away by formatting all three into one line. The editor in
/// the front end then pulled that line back apart with a regular expression so
/// it could put each error beside the control that caused it.
/// </para>
/// <para>
/// That parser is a guess at a format produced in this repository, promised by
/// nothing and asserted by nothing. These tests are the promise.
/// </para>
/// </remarks>
public sealed class SchemaViolationTests
{
    private static string SchemaRoot =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas");

    private static SchemaValidator Validator() => new(new SchemaRepository(SchemaRoot));

    private static JsonNode Species(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void AMissingPropertyNamesTheObjectItIsMissingFrom()
    {
        var result = Validator().Validate("species", 1, Species("""{"key":"x"}"""));

        result.IsValid.ShouldBeFalse();

        var required = result.Violations.FirstOrDefault(
            violation => violation.Keyword == "required");

        required.ShouldNotBeNull(
            $"no 'required' violation among: {string.Join("; ", result.Errors)}");

        // The root, spelled as the empty pointer. A missing property is a
        // failure of the object that should have held it, so this is the
        // object's location rather than the absent property's.
        required.InstanceLocation.ShouldBe("");
        required.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AFailureInsideTheDocumentPointsAtTheValue()
    {
        // The case the whole thing is for: an error that belongs beside one
        // control rather than at the top of the form.
        var document = Species("""
            {
              "key": "Not A Key",
              "name": "Test",
              "size": "Medium",
              "contentSet": "core"
            }
            """);

        var result = Validator().Validate("species", 1, document);

        result.IsValid.ShouldBeFalse();

        var placed = result.Violations.Where(violation => violation.InstanceLocation != "").ToList();

        placed.ShouldNotBeEmpty("a failure inside the document must name the value it was about");
        placed.ShouldAllBe(violation => violation.InstanceLocation.StartsWith('/'));
    }

    [Fact]
    public void EveryViolationHasACounterpartLine()
    {
        // The old field is what the command-line tool and several tests print,
        // and it stays exactly what it was: one line per violation, in the
        // same order, in the same format.
        var result = Validator().Validate("species", 1, Species("""{"key":"x"}"""));

        result.Errors.Count.ShouldBe(result.Violations.Count);

        foreach (var (line, violation) in result.Errors.Zip(result.Violations))
        {
            line.ShouldBe($"{violation.InstanceLocation}: {violation.Keyword} — {violation.Message}");
        }
    }

    [Fact]
    public void AValidDocumentHasNeither()
    {
        var result = Validator().Validate("species", 1, Species(File.ReadAllText(
            Path.Combine(SchemaRoot, "..", "content", "species", "abyssin.json"))));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Violations.ShouldBeEmpty();
    }
}
