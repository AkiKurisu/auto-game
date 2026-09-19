using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;

namespace AutoGame;

internal sealed record PreparedExecution(string ExecutionId, string AssemblyPath, string EntryType, string Generation);

internal static class BridgeClient
{
    public static async Task<JsonObject> Call(
        string connectionPath,
        string command,
        string? assembly = null,
        JsonObject? args = null,
        string? id = null,
        string? expectedGeneration = null,
        string? executionId = null,
        string? entryType = null,
        int? waitMs = null,
        bool terminate = false,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = ReadConnection(connectionPath, expectedGeneration);
        using var process = System.Diagnostics.Process.GetProcessById(ReadInt32(connection, "pid"));
        var targetStart = DateTime.Parse(connection["startUtc"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (process.HasExited || process.StartTime.ToUniversalTime() != targetStart)
            throw new InvalidOperationException("Target process changed.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds((waitMs ?? 0) + 15_000));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", ReadInt32(connection, "port"), deadline.Token);
            var request = new JsonObject {
                ["protocol"] = AttachProtocol.Version,
                ["clientId"] = clientId,
                ["hostPid"] = Environment.ProcessId,
                ["hostStartUtc"] = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"),
                ["token"] = connection["token"]!.GetValue<string>(),
                ["generation"] = connection["generation"]!.GetValue<string>(),
                ["id"] = id ?? Guid.NewGuid().ToString("N"),
                ["command"] = command,
                ["assembly"] = assembly,
                ["args"] = args?.DeepClone(),
                ["executionId"] = executionId,
                ["entryType"] = entryType,
                ["waitMs"] = waitMs,
                ["terminate"] = terminate
            };
            var stream = client.GetStream();
            WireProtocol.WriteFrame(stream, WireJson.FromJson(request));
            var result = WireJson.ToJson(WireProtocol.ReadFrame(stream))!.AsObject();
            if (result["generation"]?.GetValue<string>() != connection["generation"]!.GetValue<string>())
                throw new InvalidDataException("Unity script domain changed; reconnect before executing again.");
            if (command == "metadata" && result["state"]?.GetValue<string>() == "completed"
                && !string.Equals(Path.GetFullPath(result["result"]!["dataPath"]!.GetValue<string>()),
                    Path.GetFullPath(connection["dataPath"]!.GetValue<string>()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unity Player identity changed.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Unity bridge request timed out.");
        }
    }

    public static async Task<PreparedExecution> Prepare(string connectionPath, string code, string cacheRoot, AttachAttempt? attempt = null)
    {
        var metadata = await Call(connectionPath, "metadata");
        if (metadata["state"]!.GetValue<string>() != "completed")
            throw new InvalidOperationException(metadata.ToJsonString());
        var bridge = metadata["result"]!["bridge"]!.GetValue<string>();
        var references = metadata["result"]!["references"]!.AsArray().Select(n => n!.GetValue<string>())
            .Where(p => !Path.GetFileName(p).StartsWith("Snippet_", StringComparison.Ordinal)
                && (!Path.GetFileName(p).StartsWith("Attach_", StringComparison.Ordinal)
                    || string.Equals(p, bridge, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var directory = Path.Combine(cacheRoot, "snippets");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
        const string className = "Snippet";
        const string generatedNamespace = "AutoGame.Execution.Generated";
        File.WriteAllText(source, SnippetSourceBuilder.Build(
            className, code, source, true, "AutoGame", generatedNamespace));
        try
        {
            var generation = metadata["generation"]!.GetValue<string>();
            var assembly = TargetCompiler.Compile(source, references, Path.Combine(cacheRoot, "cache"), "Snippet_", generation);
            assembly = AttachStorage.ResolveFile(assembly, "snippet", attempt);
            AttachStorage.ValidateMonoPath(assembly, "snippet");
            return new PreparedExecution(
                "unity_" + Guid.NewGuid().ToString("N"),
                Path.GetFullPath(assembly),
                generatedNamespace + "." + className,
                generation);
        }
        finally { File.Delete(source); }
    }

    public static Task<JsonObject> Start(
        string connectionPath,
        PreparedExecution execution,
        JsonObject? args,
        CancellationToken cancellationToken = default) =>
        Call(connectionPath, "execute_start", execution.AssemblyPath, args,
            expectedGeneration: execution.Generation, executionId: execution.ExecutionId,
            entryType: execution.EntryType,
            cancellationToken: cancellationToken);

    public static Task<JsonObject> Wait(
        string connectionPath,
        string executionId,
        string generation,
        int waitMs,
        bool terminate,
        CancellationToken cancellationToken = default) =>
        Call(connectionPath, "execute_wait", expectedGeneration: generation, executionId: executionId,
            waitMs: waitMs, terminate: terminate, cancellationToken: cancellationToken);

    public static string ReadGeneration(string connectionPath) =>
        ReadConnection(connectionPath, null)["generation"]!.GetValue<string>();

    internal static string? ReadRuntimeIdentity(string connectionPath) =>
        ReadConnection(connectionPath, null)["runtimeIdentity"]?.GetValue<string>();

    internal static PlayerTargetException ReadBridgeFailure(string path)
    {
        try
        {
            var error = WireProtocol.Decode(File.ReadAllBytes(path)).AsObject();
            var code = error.TryGetValue("code", out var codeValue) ? codeValue.AsString() : "UnityBridgeInitializationFailed";
            var message = error.TryGetValue("message", out var messageValue) ? messageValue.AsString() : "Unity bridge initialization failed.";
            return new PlayerTargetException(code, message);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException)
        {
            return new PlayerTargetException("UnityBridgeInitializationFailed", "Unity bridge initialization failed and its diagnostic record was invalid.");
        }
    }

    private static JsonObject ReadConnection(string connectionPath, string? expectedGeneration)
    {
        var connection = WireJson.ToJson(WireProtocol.Decode(File.ReadAllBytes(connectionPath)))!.AsObject();
        if (ReadInt32(connection, "protocol") != AttachProtocol.Version) throw new InvalidDataException("Unsupported Unity bridge protocol.");
        if (expectedGeneration != null && connection["generation"]?.GetValue<string>() != expectedGeneration)
            throw new InvalidDataException("Unity script domain changed during execution.");
        return connection;
    }

    private static int ReadInt32(JsonObject values, string key)
    {
        var value = values[key] ?? throw new InvalidDataException("Unity bridge metadata is missing " + key + ".");
        return checked((int)value.GetValue<long>());
    }
}
