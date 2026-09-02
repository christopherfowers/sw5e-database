namespace Sw5e.Database.Schemas;

/// <summary>
/// One reason a document did not match its schema.
/// </summary>
/// <param name="InstanceLocation">
/// A JSON Pointer to the value that failed, empty for the document root.
/// </param>
/// <param name="Keyword">
/// The JSON Schema keyword that rejected it — <c>required</c>, <c>pattern</c>,
/// <c>additionalProperties</c> and so on.
/// </param>
/// <param name="Message">The validator's sentence about what was wrong.</param>
/// <remarks>
/// <para>
/// These three facts were always in hand and were being thrown away. The
/// validator formatted them into "<c>{location}: {keyword} — {message}</c>",
/// the API published that string, and the editor in the front end pulled it
/// back apart with a regular expression so it could put each error beside the
/// control that caused it.
/// </para>
/// <para>
/// That parser is careful and its failure mode is safe — an unrecognised line
/// is shown in full rather than dropped — but it is still a guess at a format
/// nothing promises. Nothing on the wire said the shape, no test asserted it,
/// and it is produced here, one repository away from the code that reads it.
/// A reworded message would have quietly stopped errors landing on fields.
/// </para>
/// </remarks>
public sealed record SchemaViolation(string InstanceLocation, string Keyword, string Message)
{
    /// <summary>
    /// The one-line form, which is what every existing caller prints.
    /// </summary>
    /// <remarks>
    /// Kept so that <see cref="SchemaValidationResult.Errors"/> stays exactly
    /// what it was. The command-line tool and several tests print these, and a
    /// structured field is worth adding on its own merits without also
    /// rewriting every consumer of the old one.
    /// </remarks>
    public override string ToString() => $"{InstanceLocation}: {Keyword} — {Message}";
}

/// <summary>
/// Whether a document matched its schema, and why not when it did not.
/// </summary>
/// <param name="Errors">
/// One line per violation. Unchanged, and still the thing to print.
/// </param>
/// <param name="Violations">
/// The same failures with their parts intact, for a caller that needs to place
/// an error rather than show it.
/// </param>
public sealed record SchemaValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<SchemaViolation> Violations)
{
    public static SchemaValidationResult Success { get; } = new(true, [], []);

    /// <summary>Builds a failure from the structured violations.</summary>
    public static SchemaValidationResult Failure(IReadOnlyList<SchemaViolation> violations) =>
        new(false, [.. violations.Select(violation => violation.ToString())], violations);

    /// <summary>
    /// Builds a failure from lines alone, for the one case that has no
    /// structure behind it: a document the validator rejected without saying
    /// which part of it was wrong.
    /// </summary>
    public static SchemaValidationResult Failure(IReadOnlyList<string> errors) =>
        new(false, errors, []);
}

public sealed class SchemaNotFoundException(string contentType, int version)
    : Exception($"No schema found for content type '{contentType}' version {version}.")
{
    public string ContentType { get; } = contentType;
    public int Version { get; } = version;
}
