using System.Text.Json.Nodes;
using Sw5e.Database.Schemas;

var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "validate":
        return Validate(args.Length > 1 ? args[1] : "schemas",
                        args.Length > 2 ? args[2] : "content");

    default:
        Console.WriteLine("""
            sw5e-database tools

            Usage:
              validate [schemaRoot] [contentRoot]   Validate all content against its schema
            """);
        return 0;
}

static int Validate(string schemaRoot, string contentRoot)
{
    var validator = new SchemaValidator(new SchemaRepository(schemaRoot));

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
        var document = JsonNode.Parse(File.ReadAllText(file));

        if (document is null)
        {
            Console.Error.WriteLine($"FAIL {file}: not valid JSON");
            failures++;
            continue;
        }

        var result = validator.Validate(contentType, 1, document);
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
