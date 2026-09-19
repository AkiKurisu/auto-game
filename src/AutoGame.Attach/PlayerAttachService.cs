using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AutoGame;

/// <summary>Local Unity Mono Player connection and target-side execution.</summary>
public sealed class PlayerAttachService(string cacheRoot, string nativeBootstrapPath) : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<TargetIdentity, byte> owned = new();
    private readonly ConcurrentDictionary<string, TargetIdentity> selected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionHandle> executions = new(StringComparer.Ordinal);
    private readonly string clientId = Guid.NewGuid().ToString("N");
    private bool disposed;
    private string? runtimeIdentity;
    private string? extractedNative;
    private string CacheRoot => resolvedCacheRoot ?? cacheRoot;
    private string? resolvedCacheRoot;
    private string NativeBootstrapPath => extractedNative ??=
        string.IsNullOrEmpty(nativeBootstrapPath) ? AttachStorage.ExtractNative(CacheRoot) : nativeBootstrapPath;
    private string RuntimeIdentity => runtimeIdentity ??= AttachStorage.RuntimeIdentity(NativeBootstrapPath);

    /// <summary>Creates an Attach client using the shared per-user rendezvous.</summary>
    public static PlayerAttachService CreateDefault() => new(AttachStorage.DefaultRoot, "");

    /// <summary>Lists running local Unity Mono Players without attaching.</summary>
    public object List()
    {
        return PlayerDiscovery.List().Select(target => new
        {
            target.Pid,
            target.StartedUtc,
            executable = target.Executable,
            dataPath = AttachStorage.DataPath(Connection(target.Pid)) ?? target.DataPath,
            target.ManagedPath,
            target.MonoModule,
            target.UnityPlayerModule
        }).ToArray();
    }

    private string Connection(int pid) => AttachStorage.Connection(CacheRoot, pid);

    /// <summary>Returns the managed assembly directory for a validated Unity Mono Player.</summary>
    public string GetManagedPath(int pid) => PlayerDiscovery.Get(pid).ManagedPath;

    /// <summary>Reads process and bridge state for an explicit Player PID without attaching.</summary>
    public async Task<JsonObject> Status(int pid)
    {
        var player = PlayerDiscovery.Get(pid);
        var path = Connection(pid);
        if (File.Exists(path))
        {
            try { return await BridgeClient.Call(path, "metadata"); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Sockets.SocketException
                or InvalidOperationException or ArgumentException or TimeoutException) { }
        }
        return new JsonObject
        {
            ["state"] = "completed",
            ["result"] = new JsonObject
            {
                ["attached"] = false,
                ["pid"] = player.Pid,
                ["executable"] = player.Executable,
                ["dataPath"] = player.DataPath,
                ["managedPath"] = player.ManagedPath
            }
        };
    }

    /// <summary>Attaches as needed and executes code against an explicit Player PID.</summary>
    public async Task<JsonObject> Execute(int pid, string code, JsonObject? args, bool runInBackground,
        int yieldTimeMs, CancellationToken cancellationToken = default)
    {
        var key = "pid:" + pid;
        await Connect(key, pid, cancellationToken);
        return await Execute(key, code, args, runInBackground, yieldTimeMs, cancellationToken);
    }

    /// <summary>Waits for an execution using the current bridge generation for an explicit PID.</summary>
    public Task<JsonObject> Wait(int pid, string executionId, int yieldTimeMs, bool terminate,
        CancellationToken cancellationToken = default)
    {
        _ = PlayerDiscovery.Get(pid);
        var path = Connection(pid);
        if (!File.Exists(path)) return Task.FromResult(Lost(executionId, ""));
        return BridgeClient.Wait(path, executionId, BridgeClient.ReadGeneration(path), NormalizeWait(yieldTimeMs), terminate, cancellationToken);
    }

    /// <summary>Stops the injected managed bridge for an explicit PID.</summary>
    public async Task<JsonObject> Disconnect(int pid)
    {
        _ = PlayerDiscovery.Get(pid);
        var path = Connection(pid);
        if (!File.Exists(path)) return new JsonObject { ["state"] = "completed", ["result"] = "detached" };
        return await BridgeClient.Call(path, "stop");
    }

    /// <summary>Attaches or restores the bridge without replaying an execution.</summary>
    public async Task<JsonObject> Connect(string threadId, int? requestedPid, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        AttachAttempt? attempt = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var pid = requestedPid ?? RequireTarget(threadId).Pid;
            using var target = GetUnityProcess(pid);
            var player = PlayerDiscovery.Get(pid);
            TargetCapabilityValidator.Validate(player.ManagedPath);
            var identity = new TargetIdentity(pid, target.StartTime.ToUniversalTime());
            attempt = new AttachAttempt(pid);
            attempt.Step("resolve-paths");
            resolvedCacheRoot ??= AttachStorage.ResolveRoot(cacheRoot, attempt);
            var path = Connection(pid);
            AttachStorage.ValidateMonoPath(path, "session and temporary state", ".bootstrap".Length + 33);
            AttachStorage.ValidateMonoPath(Path.Combine(CacheRoot, "cache", "Snippet_" + new string('0', 64) + ".dll"), "compiled assembly");
            var logicalConnection = AttachStorage.Connection(cacheRoot, pid);
            AttachStorage.CheckStateAliases(logicalConnection, path, attempt);
            using var processLock = await AttachStorage.Lock(path, cancellationToken);
            var journal = path + ".bootstrap";
            attempt.SetJournal(journal);
            attempt.Step("connect");
            if (File.Exists(path))
            {
                var recordedIdentity = BridgeClient.ReadRuntimeIdentity(path);
                if (recordedIdentity != RuntimeIdentity) throw new PlayerTargetException("UnityRuntimeVersionMismatch", "The selected Player has a different AutoGame runtime. Restart the game to upgrade it; no injection or shutdown was sent.");
                try
                {
                    var current = await BridgeClient.Call(path, "metadata");
                    if (current["state"]?.GetValue<string>() != "completed")
                        throw new IOException("The bridge has not completed its main-thread metadata handshake.");
                    if (current["result"]?["asyncExecution"]?.GetValue<bool>() == true)
                    {
                        ValidateRuntime(current);
                        if (File.Exists(journal)) File.Delete(journal);
                        await RecordSelection(threadId, identity, current);
                        return current;
                    }
                    throw new PlayerTargetException("UnityRuntimeVersionMismatch", "The existing Unity bridge does not support this runtime. Restart the game to upgrade it; no bridge was stopped or injected.");
                }
                catch (Exception e) when (e is not PlayerTargetException && (e is IOException or InvalidDataException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)) { }
            }
            if (File.Exists(path + ".lifecycle") && JsonNode.Parse(File.ReadAllText(path + ".lifecycle"))?["stage"]?.GetValue<int>() != 200)
            {
                attempt.Step("recovery-handshake");
                return await WaitForHandshake(threadId, identity, target, path, journal, cancellationToken);
            }
            if (!File.Exists(NativeBootstrapPath)) throw new FileNotFoundException("Native bootstrap is not configured.", NativeBootstrapPath);
            if (File.Exists(journal))
            {
                if (AttachAttempt.CanRetry(JsonNode.Parse(File.ReadAllText(journal)))) File.Delete(journal);
                else throw new PlayerTargetException("UnityAttachOutcomeUnknown", "Bootstrap outcome is unknown. No new bootstrap was sent; wait for its handshake or restart the isolated test session.");
            }
            attempt.Step("prepare-payload");
            var payloadSource = Path.Combine(CacheRoot, "payload", RuntimeIdentity);
            Directory.CreateDirectory(payloadSource);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var owner = typeof(PlayerAttachService).Assembly;
            foreach (var name in owner.GetManifestResourceNames().Where(n => n.EndsWith(".cs", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(owner.GetManifestResourceStream(name)!);
                AttachStorage.WriteSource(Path.Combine(payloadSource, name), reader.ReadToEnd());
            }
            AttachStorage.WriteSource(Path.Combine(payloadSource, "RuntimeIdentity.cs"),
                "namespace AutoGame { internal static class RuntimeIdentity { public const string Value = \"" + RuntimeIdentity
                + "\"; public const string Version = \"" + AttachStorage.ProductVersion + "\"; } }");
            var references = Directory.GetFiles(player.ManagedPath, "*.dll")
                .Where(file => Path.GetFileName(file).StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).StartsWith("System", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase));
            string payload;
            try { payload = TargetCompiler.Compile(payloadSource, references, Path.Combine(CacheRoot, "cache")); }
            catch (InvalidOperationException error)
            {
                throw new PlayerTargetException("UnityTargetUnsupportedRuntimeProfile",
                    "The Unity Player cannot compile the dependency-free bridge against its managed profile. " + error.Message);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path + ".error")) File.Delete(path + ".error");
            NativeInjector.Inject(pid, NativeBootstrapPath, payload, path, attempt);
            owned.TryAdd(identity, 0);
            attempt.Step("handshake");
            var connected = await WaitForHandshake(threadId, identity, target, path, journal, cancellationToken);
            attempt.Step("connected");
            return connected;
        }
        catch (Exception error)
        {
            attempt?.Failed(error);
            if (attempt != null && error is not OperationCanceledException)
                throw attempt.DescribeFailure(error);
            throw;
        }
        finally { gate.Release(); }
    }

    /// <summary>Reads live connection metadata without reconnecting.</summary>
    public Task<JsonObject> Status(string threadId)
    {
        var target = RequireTarget(threadId);
        return BridgeClient.Call(Connection(target.Pid), "metadata");
    }

    /// <summary>Compiles for the target and starts one execution; failures are never replayed.</summary>
    public async Task<JsonObject> Execute(
        string threadId,
        string code,
        JsonObject? args,
        bool runInBackground,
        int yieldTimeMs,
        CancellationToken cancellationToken = default)
    {
        var target = RequireTarget(threadId);
        await Connect(threadId, target.Pid, cancellationToken);
        var connection = Connection(target.Pid);
        var prepared = await BridgeClient.Prepare(connection, code, CacheRoot, new AttachAttempt(target.Pid));
        var handle = new ExecutionHandle(threadId, target, prepared.Generation, connection);
        PruneExecutions();
        if (executions.Count >= 4096) throw new PlayerTargetException("UnityExecutionCapacity", "This client has too many unresolved executions.");
        executions[prepared.ExecutionId] = handle;
        try
        {
            var started = await BridgeClient.Start(connection, prepared, args, cancellationToken);
            if (IsTerminal(started)) return RecordResult(prepared.ExecutionId, started);
            if (runInBackground)
            {
                try
                {
                    return RecordResult(prepared.ExecutionId, await BridgeClient.Wait(connection, prepared.ExecutionId, prepared.Generation,
                        NormalizeWait(yieldTimeMs), terminate: false, cancellationToken: CancellationToken.None));
                }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException)
                {
                    return Unknown(prepared.ExecutionId, prepared.Generation);
                }
            }

            while (true)
            {
                var result = await BridgeClient.Wait(connection, prepared.ExecutionId, prepared.Generation,
                    1000, terminate: false, cancellationToken: cancellationToken);
                if (IsTerminal(result)) return RecordResult(prepared.ExecutionId, result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await BridgeClient.Wait(connection, prepared.ExecutionId, prepared.Generation, 0, terminate: true, cancellationToken: CancellationToken.None); }
            catch { }
            throw;
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return Unknown(prepared.ExecutionId, prepared.Generation);
        }
    }

    /// <summary>Waits for or cooperatively cancels an execution created by this task.</summary>
    public async Task<JsonObject> Wait(
        string threadId,
        string executionId,
        int yieldTimeMs,
        bool terminate,
        CancellationToken cancellationToken = default)
    {
        PruneExecutions();
        if (!executions.TryGetValue(executionId, out var handle)) return Lost(executionId, "");
        if (!string.Equals(handle.ThreadId, threadId, StringComparison.Ordinal))
            throw new PlayerTargetException("UnityExecutionUnavailable", "The Unity execution does not belong to this task or is no longer available.");

        try
        {
            using var process = GetUnityProcess(handle.Target.Pid);
            if (process.StartTime.ToUniversalTime() != handle.Target.StartedUtc)
                return RecordResult(executionId, Lost(executionId, handle.Generation));
            if (!File.Exists(handle.ConnectionPath)
                || !string.Equals(BridgeClient.ReadGeneration(handle.ConnectionPath), handle.Generation, StringComparison.Ordinal))
                return RecordResult(executionId, Lost(executionId, handle.Generation));
            return RecordResult(executionId, await BridgeClient.Wait(handle.ConnectionPath, executionId, handle.Generation,
                NormalizeWait(yieldTimeMs), terminate, cancellationToken));
        }
        catch (PlayerTargetException) { return RecordResult(executionId, Lost(executionId, handle.Generation)); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException or TimeoutException)
        {
            return RecordResult(executionId, Lost(executionId, handle.Generation));
        }
    }

    /// <summary>Releases this task selection without stopping other clients.</summary>
    public async Task<JsonObject> Disconnect(string threadId)
    {
        var target = RequireTarget(threadId);
        selected.TryRemove(threadId, out _);
        if (selected.Values.Contains(target))
            return new JsonObject { ["state"] = "completed", ["result"] = "detached" };
        var result = await BridgeClient.Call(Connection(target.Pid), "detach", clientId: clientId);
        if (result["state"]?.GetValue<string>() == "completed") owned.TryRemove(target, out _);
        return result;
    }

    private static Process GetUnityProcess(int pid)
    {
        Process target;
        try { target = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new PlayerTargetException("UnityTargetUnavailable", "The selected Unity Player is no longer running."); }
        try
        {
            if (target.HasExited) throw new PlayerTargetException("UnityTargetUnavailable", "The selected process is no longer running.");
            _ = PlayerDiscovery.Get(pid);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private TargetIdentity RequireTarget(string threadId)
    {
        if (!selected.TryGetValue(threadId, out var selection))
            throw new PlayerTargetException("UnityTargetRequired", "No Unity Player is connected for this client.");
        try
        {
            using var process = GetUnityProcess(selection.Pid);
            if (process.StartTime.ToUniversalTime() == selection.StartedUtc) return selection;
        }
        catch (PlayerTargetException) { }
        RemoveSelections(selection);
        throw new PlayerTargetException("UnityTargetUnavailable", "The selected Unity Player is no longer running.");
    }

    private async Task RecordSelection(string threadId, TargetIdentity identity, JsonObject response)
    {
        if (response["state"]?.GetValue<string>() != "completed") return;
        ValidateRuntime(response);
        await BridgeClient.Call(Connection(identity.Pid), "lease", clientId: clientId);
        selected[threadId] = identity;
        owned.TryAdd(identity, 0);
    }

    private async Task<JsonObject> WaitForHandshake(
        string threadId,
        TargetIdentity identity,
        Process target,
        string path,
        string journal,
        CancellationToken cancellationToken)
    {
        var response = await AttachHandshake.WaitAsync(async token =>
        {
            target.Refresh();
            if (target.HasExited)
                throw new PlayerTargetException("UnityTargetUnavailable", "The selected Unity Player exited before completing its main-thread handshake.");
            if (File.Exists(path + ".error")) throw BridgeClient.ReadBridgeFailure(path + ".error");
            if (!File.Exists(path)) throw new IOException("Unity has not published its bridge metadata.");
            var result = await BridgeClient.Call(path, "metadata", cancellationToken: token);
            if (result["state"]?.GetValue<string>() != "completed")
                throw new IOException("Unity has not completed its main-thread handshake.");
            ValidateRuntime(result);
            return result;
        }, HandshakeTimeout, cancellationToken);
        if (File.Exists(journal)) File.Delete(journal);
        await RecordSelection(threadId, identity, response);
        return response;
    }

    private void ValidateRuntime(JsonObject response)
    {
        if (response["result"]?["runtimeIdentity"]?.GetValue<string>() != RuntimeIdentity)
            throw new PlayerTargetException("UnityRuntimeVersionMismatch", "The running Unity bridge belongs to a different runtime build. Restart the game to upgrade it; no injection or shutdown was sent.");
    }

    private void RemoveSelections(TargetIdentity identity)
    {
        foreach (var pair in selected.Where(pair => pair.Value == identity))
            selected.TryRemove(pair);
    }

    /// <summary>Releases this client from its connected bridges without reconnecting during cleanup.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await Task.WhenAll(owned.Keys.Select(async target =>
            {
                try { await BridgeClient.Call(Connection(target.Pid), "detach", clientId: clientId); }
                catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException
                    or ArgumentException or TimeoutException or System.Net.Sockets.SocketException) { }
            }));
            selected.Clear();
            executions.Clear();
        }
        finally { gate.Release(); }
    }

    private readonly record struct TargetIdentity(int Pid, DateTime StartedUtc);
    private sealed record ExecutionHandle(string ThreadId, TargetIdentity Target, string Generation, string ConnectionPath)
    {
        public DateTime? TerminalUtc { get; set; }
    }

    private JsonObject RecordResult(string executionId, JsonObject result)
    {
        if (IsTerminal(result) && executions.TryGetValue(executionId, out var handle)) handle.TerminalUtc ??= DateTime.UtcNow;
        PruneExecutions();
        return result;
    }

    private void PruneExecutions()
    {
        var terminal = executions.Where(p => p.Value.TerminalUtc != null).OrderBy(p => p.Value.TerminalUtc).ToArray();
        foreach (var item in terminal.Take(Math.Max(0, terminal.Length - 1024)).Concat(terminal.Where(p => p.Value.TerminalUtc < DateTime.UtcNow.AddMinutes(-10))))
            executions.TryRemove(item.Key, out _);
    }

    private static int NormalizeWait(int waitTimeMs) => Math.Clamp(waitTimeMs, 0, 30_000);

    private static bool IsTerminal(JsonObject result) => result["state"]?.GetValue<string>() is "completed" or "failed" or "cancelled" or "lost";

    private static JsonObject Lost(string executionId, string generation) => new()
    {
        ["state"] = "lost",
        ["executionId"] = executionId,
        ["generation"] = generation
    };

    private static JsonObject Unknown(string executionId, string generation) => new()
    {
        ["state"] = "unknown",
        ["executionId"] = executionId,
        ["generation"] = generation
    };
}

/// <summary>A stable target or execution selection error.</summary>
public sealed class PlayerTargetException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>The stable machine-readable error code.</summary>
    public string Code { get; } = code;
}
