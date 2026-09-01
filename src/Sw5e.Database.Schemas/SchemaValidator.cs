using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Sw5e.Database.Schemas;

/// <summary>
/// Validates content payloads against their registered schema. This type is
/// shared by the import tooling and the API so that build-time and runtime
/// validation can never diverge.
/// </summary>
/// <remarks>
/// <para>
/// That sharing is literal, not aspirational. <c>sw5e-api</c> pins this
/// repository as a submodule and references this project, so the validator
/// that gates a contributor's write is the same object that gates the corpus
/// on every pull request here.
/// </para>
/// <para>
/// <b>Changing this class changes what the API accepts.</b> The evaluation
/// options below are part of that contract:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>OutputFormat.List</c> is what makes every failure in a document
/// reportable individually. The API hands that list to whoever is authoring the
/// document so an editor can put each failure beside the field that caused it;
/// under <c>Flag</c> the document is still rejected and the author is told
/// nothing useful. Pinned by a test.
/// </item>
/// <item>
/// <c>RequireFormatValidation</c> makes <c>format</c> assertive rather than
/// annotative. Turning it off would silently widen what both this repository
/// and the API accept.
/// </item>
/// </list>
/// <para>
/// <see cref="SchemaRepository"/>'s
/// <c>{root}/{contentType}/v{version}.json</c> layout is part of the same
/// contract: the API resolves which schema version a type is on by reading that
/// directory.
/// </para>
/// </remarks>
public sealed class SchemaValidator(SchemaRepository repository)
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    };

    public SchemaValidationResult Validate(string contentType, int version, JsonNode document)
    {
        var schema = repository.Get(contentType, version);
        var instance = document.Deserialize<JsonElement>();
        var evaluation = schema.Evaluate(instance, Options);

        if (evaluation.IsValid)
        {
            return SchemaValidationResult.Success;
        }

        var errors = Flatten(evaluation).ToList();

        return SchemaValidationResult.Failure(
            errors.Count > 0 ? errors : ["Document did not conform to schema."]);
    }

    private static IEnumerable<string> Flatten(EvaluationResults results)
    {
        if (!results.IsValid && results.Errors is { Count: > 0 })
        {
            foreach (var (keyword, message) in results.Errors)
            {
                yield return $"{results.InstanceLocation}: {keyword} — {message}";
            }
        }

        foreach (var child in results.Details ?? [])
        {
            foreach (var error in Flatten(child))
            {
                yield return error;
            }
        }
    }
}
