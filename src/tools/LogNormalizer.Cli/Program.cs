using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaZulu.Normalize;

var jsonOptions = new JsonSerializerOptions {
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

string? rulebasePath = null;
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

if (rulebasePath == null)
{
    Console.Error.WriteLine("error: -r <rulebase-file-or-dir> is required");
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

if (ctx.LoadSamples(rulebasePath) != 0)
{
    Console.Error.WriteLine($"error: failed to load rulebase '{rulebasePath}'");
    return 1;
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
        usage: lognormalizer -r <rulebase-file-or-dir> [-m <message>] [-O] [--add-rule] [-T]

          -r <path>    v2 rulebase file to load, or a directory whose rulebase
                       files are all loaded (recursively) into one combined
                       rulebase (required)
          -m <message> normalize a single message instead of reading stdin
          -O           always add the original message to the output
          --add-rule   add a mock-up of the matching rule to the output metadata
          -T           include the internal 'event.tags' field in the JSON output
                       (stripped by default, matching the reference CLI)

        Without -m, messages are read one per line from stdin and one JSON
        object is printed per line to stdout.
        """);
}
