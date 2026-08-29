namespace Sw5e.Database.Schemas;

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static SchemaValidationResult Success { get; } = new(true, []);

    public static SchemaValidationResult Failure(IReadOnlyList<string> errors) =>
        new(false, errors);
}

public sealed class SchemaNotFoundException(string contentType, int version)
    : Exception($"No schema found for content type '{contentType}' version {version}.")
{
    public string ContentType { get; } = contentType;
    public int Version { get; } = version;
}
