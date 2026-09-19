using System.Text.Json;
using System.Text.Json.Nodes;
using AutoGame;
using AutoGame.Decompiler;

return await ProgramEntry.Run(args);

internal static class ProgramEntry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();
            if (args[0] is "version" or "--version") return Write(new { ok = true, command = "version", result = "0.1.0" });
            var options = Options.Parse(args.Skip(1));
            var service = PlayerAttachService.CreateDefault();
            object result = args[0] switch
            {
                "list" => service.List(),
                "status" => await service.Status(options.RequiredPid()),
                "attach" => await service.Connect("cli:" + options.RequiredPid(), options.RequiredPid()),
                "exec" => await service.Execute(options.RequiredPid(), options.Code(), options.JsonObject("args"),
                    options.Flag("background"), options.Int("yield-time", 1000)),
                "wait" => await service.Wait(options.RequiredPid(), options.Required("execution"),
                    options.Int("yield-time", 1000), options.Flag("terminate")),
                "detach" => await service.Disconnect(options.RequiredPid()),
                "decompile" => GameDecompiler.Decompile(service.GetManagedPath(options.RequiredPid()),
                    Path.GetFullPath(options.Required("output")), options.Value("assembly"), options.Flag("all")),
                _ => throw new ArgumentException("Unknown command: " + args[0])
            };
            return Write(new { ok = true, command = args[0], result });
        }
        catch (Exception error)
        {
            var code = error is PlayerTargetException target ? target.Code : error.GetType().Name;
            Write(new { ok = false, error = new { code, message = error.Message } });
            return 1;
        }
    }

    private static int Write(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
Auto Game CLI - automate a running game on Windows x64 (Unity Mono)

Commands:
  auto-game list
  auto-game status --pid <pid>
  auto-game attach --pid <pid>
  auto-game exec --pid <pid> (--code <csharp> | --file <path>) [--args <json>] [--background] [--yield-time <ms>]
  auto-game wait --pid <pid> --execution <id> [--yield-time <ms>] [--terminate]
  auto-game detach --pid <pid>
  auto-game decompile --pid <pid> --output <dir> (--assembly <name.dll> | --all)
  auto-game version
""");
        return 0;
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> values;
    private Options(Dictionary<string, string?> values) => this.values = values;

    public static Options Parse(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument: " + args[index]);
            var name = args[index][2..];
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) values[name] = args[++index];
            else values[name] = null;
        }
        return new Options(values);
    }

    public string? Value(string name) => values.TryGetValue(name, out var value) ? value : null;
    public bool Flag(string name) => values.ContainsKey(name);
    public string Required(string name) => Value(name) ?? throw new ArgumentException("Missing --" + name + ".");
    public int RequiredPid() => Int("pid", null) ?? throw new ArgumentException("Missing --pid.");
    public int Int(string name, int fallback) => Int(name, (int?)fallback)!.Value;
    private int? Int(string name, int? fallback) => Value(name) is { } value ? int.Parse(value) : fallback;
    public string Code() => Value("code") ?? (Value("file") is { } file ? File.ReadAllText(file) : throw new ArgumentException("Specify --code or --file."));
    public JsonObject? JsonObject(string name) => Value(name) is { } value ? JsonNode.Parse(value)?.AsObject() : null;
}
