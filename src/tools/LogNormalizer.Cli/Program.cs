using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaZulu.Normalize;

var jsonOptions = new JsonSerializerOptions {
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

string? rulebasePath = null;
string? importBinaryPath = null;
string? exportBinaryPath = null;
string? singleMessage = null;
var addOriginalMsg = false;
var addRule = false;
var includeEventTags = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-r":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("error: -r requires a rulebase file or directory argument");
                PrintUsage();
                return 1;
            }
            rulebasePath = args[++i];
            break;

        case "-m":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("error: -m requires a message argument");
                PrintUsage();
                return 1;
            }
            singleMessage = args[++i];
            break;

        case "--import-binary":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("error: --import-binary requires a compiled PDAG file argument");
                PrintUsage();
                return 1;
            }
            importBinaryPath = args[++i];
            break;

        case "--export-binary":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("error: --export-binary requires an output file argument");
                PrintUsage();
                return 1;
            }
            exportBinaryPath = args[++i];
            break;

        case "-O":
            addOriginalMsg = true;
            break;

        case "--add-rule":
            addRule = true;
            break;

        case "-T":
            includeEventTags = true;
            break;

        case "-h":
        case "--help":
            PrintUsage();
            return 0;

        default:
            Console.Error.WriteLine($"unknown option: {args[i]}");
            PrintUsage();
            return 1;
    }
}

if (rulebasePath == null && importBinaryPath == null)
{
    Console.Error.WriteLine("error: either -r <rulebase-file-or-dir> or --import-binary <compiled-pdag-file> is required");
    PrintUsage();
    return 1;
}

if (rulebasePath != null && importBinaryPath != null)
{
    Console.Error.WriteLine("error: specify only one rulebase source: -r or --import-binary");
    PrintUsage();
    return 1;
}

var ctx = new LogNormContext {
    ErrorCallback = msg => Console.Error.WriteLine($"lognormalizer: {msg}"),
};
if (addOriginalMsg)
{
    ctx.Options |= LogNormOptions.AddOriginalMessage;
}

if (addRule)
{
    ctx.Options |= LogNormOptions.AddRule;
}

if (importBinaryPath != null)
{
    try
    {
        ctx.ImportCompiledPdag(importBinaryPath);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"error: failed to import binary rulebase '{importBinaryPath}': {ex.Message}");
        return 1;
    }
}
else if (ctx.LoadSamples(rulebasePath!) != 0)
{
    Console.Error.WriteLine($"error: failed to load rulebase '{rulebasePath}'");
    return 1;
}

if (exportBinaryPath != null)
{
    try
    {
        ctx.ExportCompiledPdag(exportBinaryPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"error: failed to export binary rulebase '{exportBinaryPath}': {ex.Message}");
        return 1;
    }
}

if (singleMessage != null)
{
    NormalizeAndPrint(ctx, singleMessage, includeEventTags, jsonOptions);
    return 0;
}

string? line;
while ((line = Console.In.ReadLine()) != null)
{
    NormalizeAndPrint(ctx, line, includeEventTags, jsonOptions);
}

return 0;

static void NormalizeAndPrint(LogNormContext ctx, string message, bool includeEventTags, JsonSerializerOptions jsonOptions)
{
    ctx.Normalize(message, out JsonObject json);
    if (!includeEventTags)
    {
        json.Remove("event.tags");
    }

    Console.WriteLine(json.ToJsonString(jsonOptions));
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        usage: lognormalizer (-r <rulebase-file-or-dir> | --import-binary <compiled-pdag-file>) [--export-binary <compiled-pdag-file>] [-m <message>] [-O] [--add-rule] [-T]

          -r <path>    v2 rulebase file to load, or a directory whose rulebase
                       files are all loaded (recursively) into one combined
                       rulebase
          --import-binary <path>
                       load a previously exported compiled PDAG binary instead
                       of parsing and compiling a text rulebase
          --export-binary <path>
                       write the compiled PDAG binary after loading/importing
                       the rulebase
          -m <message> normalize a single message instead of reading stdin
          -O           always add the original message to the output
          --add-rule   add a mock-up of the matching rule to the output metadata
          -T           include the internal 'event.tags' field in the JSON output
                       (stripped by default, matching the reference CLI)

        Without -m, messages are read one per line from stdin and one JSON
        object is printed per line to stdout.
        """);
}
