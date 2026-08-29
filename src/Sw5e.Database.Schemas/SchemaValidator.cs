using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Sw5e.Database.Schemas;

/// <summary>
/// Validates content payloads against their registered schema. This type is
/// shared by the import tooling and the API so that build-time and runtime
/// validation can never diverge.
/// </summary>
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
