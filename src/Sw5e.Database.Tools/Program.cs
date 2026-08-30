using System.Text.Json;
using System.Text.Json.Nodes;
using Sw5e.Database.Schemas;
using Sw5e.Database.Tools.Legacy;

var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "validate":
        return Validate(args.Length > 1 ? args[1] : "schemas",
                        args.Length > 2 ? args[2] : "content");

    case "import-legacy":
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "ERROR import-legacy needs the path to the legacy archive, e.g. " +
                "'import-legacy ../sw5e-legacy-archive/api content'.");
            return 1;
        }

        return ImportLegacy(args[1], args.Length > 2 ? args[2] : "content");

    default:
        Console.WriteLine("""
            sw5e-database tools

            Usage:
              validate [schemaRoot] [contentRoot]      Validate all content against its schema
              import-legacy <archiveRoot> [contentRoot]
                  Rewrite the enhanced-item, weapon-property, armor-property, rule
                  and reference-table content from the 2022 legacy archive, repairing
                  the archive's encoding damage on the way through. Re-runnable and
                  deterministic: the same archive produces byte-identical documents.
            """);
        return 0;
}

// Runs the legacy import and reports what it did. The counts matter more than
// the exit code: an import that silently wrote a hundred documents where two
// thousand were expected looks exactly like a successful one.
static int ImportLegacy(string archiveRoot, string contentRoot)
{
    if (!Directory.Exists(archiveRoot))
    {
        Console.Error.WriteLine($"ERROR No legacy archive at '{archiveRoot}'.");
        return 1;
    }

    ImportReport report;

    try
    {
        report = LegacyImporter.Import(archiveRoot, contentRoot);
    }
    catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
    {
        Console.Error.WriteLine($"ERROR {error.Message}");
        return 1;
    }

    foreach (var group in report.Written
                 .GroupBy(path => path[..path.IndexOf('/')])
                 .OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"{group.Key,-18} {group.Count(),5} document(s)");
    }

    Console.WriteLine($"{"total",-18} {report.Written.Count,5} document(s)");

    if (report.Skipped.Count > 0)
    {
        Console.WriteLine($"\nNot imported ({report.Skipped.Count}):");

        foreach (var skipped in report.Skipped)
        {
            Console.WriteLine($"  {skipped}");
        }
    }

    if (report.Unrepaired.Count > 0)
    {
        // Surfaced rather than suppressed. Every one of these is a character
        // the archive lost and no rule can recover without guessing; the list
        // is asserted in the test suite so it cannot grow unnoticed.
        Console.WriteLine(
            $"\nStill carrying unrecoverable characters ({report.Unrepaired.Count} document(s)):");

        foreach (var loss in report.Unrepaired)
        {
            Console.WriteLine($"  {loss}");
        }
    }

    return 0;
}

static int Validate(string schemaRoot, string contentRoot)
{
    SchemaValidator validator;

    try
    {
        validator = new SchemaValidator(new SchemaRepository(schemaRoot));
    }
    catch (DirectoryNotFoundException)
    {
        Console.Error.WriteLine(
            $"ERROR No schema directory at '{schemaRoot}'. Pass the schema root as " +
            "the first argument, e.g. 'validate schemas content'.");
        return 1;
    }

    if (!Directory.Exists(contentRoot))
    {
        Console.WriteLine($"No content directory at '{contentRoot}'; nothing to validate.");
        return 0;
    }

    var failures = 0;
    var checkedCount = 0;

    foreach (var file in Directory.EnumerateFiles(contentRoot, "*.json", SearchOption.AllDirectories))
    {
        var contentType = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";

        JsonNode? document;

        try
        {
            document = JsonNode.Parse(File.ReadAllText(file));
        }
        catch (JsonException error)
        {
            Console.Error.WriteLine($"FAIL {file}: not valid JSON - {error.Message}");
            failures++;
            continue;
        }

        if (document is null)
        {
            Console.Error.WriteLine($"FAIL {file}: not valid JSON");
            failures++;
            continue;
        }

        SchemaValidationResult result;

        try
        {
            result = validator.Validate(contentType, 1, document);
        }
        catch (SchemaNotFoundException)
        {
            // A contributor's first content PR lands here whenever the
            // directory name does not match a schema. Report it as a normal
            // validation failure rather than crashing with a stack trace.
            Console.Error.WriteLine(
                $"FAIL {file}: no schema for content type '{contentType}' version 1. " +
                $"Content must live in '{contentRoot}/<content-type>/' where " +
                $"<content-type> matches a directory under '{schemaRoot}/'.");
            failures++;
            continue;
        }

        checkedCount++;

        if (!result.IsValid)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {file}");

            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"     {error}");
            }
        }
    }

    Console.WriteLine($"Validated {checkedCount} file(s), {failures} failure(s).");
    return failures == 0 ? 0 : 1;
}
