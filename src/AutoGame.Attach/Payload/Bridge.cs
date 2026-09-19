#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AutoGame
{
    public static class Bridge
    {
        sealed class Work
        {
            public IDictionary<string, AutoGameValue> Request;
            public string State = "queued";
            public AutoGameValue Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        }

        sealed class Execution
        {
            public string Id;
            public string AssemblyPath;
            public string EntryType;
            public AutoGameValue Args;
            public string State = "queued";
            public AutoGameValue Result;
            public string Error;
            public string ErrorCode;
            public Task<object> Task;
            public bool CancellationRequested;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly ManualResetEventSlim Changed = new ManualResetEventSlim();
            public readonly DateTime StartedUtc = DateTime.UtcNow;
        }

        sealed class ClientLease { public int Pid; public DateTime StartUtc; }

        static readonly object Sync = new object();
        static readonly Queue<Work> Queue = new Queue<Work>();
        static readonly Queue<Action> MainThreadActions = new Queue<Action>();
        static readonly Dictionary<string, Work> Requests = new Dictionary<string, Work>();
        static readonly Dictionary<string, ClientLease> Clients = new Dictionary<string, ClientLease>();
        static readonly Dictionary<string, Execution> Executions = new Dictionary<string, Execution>();
        static UnityContinuationScheduler Scheduler = new UnityContinuationScheduler(() => Time.realtimeSinceStartup);
        [DllImport("AutoGame.Native.dll")] static extern ulong GetDomainEpoch();

        static TcpListener listener;
        static GameObject pumpObject;
        static BridgePump pump;
        static volatile bool active;
        static int failing, cleanupPending;
        static string connectionPath, leasePath, token, generation;
        static int mainThread, activeExecutionCount;
        static double nextLeaseCheck;
        static bool savedRunInBackground;

        public static void Start(string path, uint bootstrapThread, uint nativeMain)
        {
            if (active) return;
            connectionPath = path;
            leasePath = path + ".leases";
            Interlocked.Exchange(ref failing, 0);
            try
            {
                CleanupUnityState();
                ResetState();
                LoadClients();
                mainThread = Thread.CurrentThread.ManagedThreadId;
                token = Guid.NewGuid().ToString("N");
                generation = Guid.NewGuid().ToString("N");
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                pumpObject = new GameObject("__AutoGameBridge");
                pumpObject.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(pumpObject);
                pump = pumpObject.AddComponent<BridgePump>();
                pump.enabled = false;
                active = true;
                PublishConnection(bootstrapThread, nativeMain);
                pump.enabled = true;
                new Thread(Accept) { IsBackground = true, Name = "AutoGame Bridge" }.Start();
            }
            catch (Exception error)
            {
                Fail("UnityBridgeStartFailed", error);
            }
        }

        public static void WriteBootstrapError(string path, Exception error)
        {
            connectionPath = path;
            if (Interlocked.Exchange(ref failing, 1) != 0) return;
            WriteError("UnityBridgeBootstrapFailed", error);
        }

        static void PublishConnection(uint bootstrapThread, uint nativeMain)
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var metadata = WireMap.Object(
                "protocol", AttachProtocol.Version,
                "pid", process.Id,
                "startUtc", process.StartTime.ToUniversalTime().ToString("O"),
                "port", ((IPEndPoint)listener.LocalEndpoint).Port,
                "token", token,
                "generation", generation,
                "bridgeSession", generation,
                "domainEpoch", GetDomainEpoch(),
                "runtimeIdentity", RuntimeIdentity.Value,
                "runtimeVersion", RuntimeIdentity.Version,
                "executable", process.MainModule.FileName,
                "dataPath", Application.dataPath,
                "version", Application.unityVersion,
                "nativeMain", nativeMain,
                "bootstrapThread", bootstrapThread,
                "mainThread", mainThread,
                "context", SynchronizationContext.Current == null ? null : SynchronizationContext.Current.GetType().FullName);
            var temporary = connectionPath + ".tmp";
            File.WriteAllBytes(temporary, WireProtocol.Encode(metadata));
            if (File.Exists(connectionPath)) File.Delete(connectionPath);
            File.Move(temporary, connectionPath);
        }

        static void Accept()
        {
            while (active)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => Handle(client));
                }
                catch (SocketException) { if (active) Fail("UnityBridgeAcceptFailed", null); break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        static void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = client.SendTimeout = 35000;
                    var stream = client.GetStream();
                    var request = WireProtocol.ReadFrame(stream).AsObject();
                    if (WireMap.Int64(request, "protocol", 0) != AttachProtocol.Version) return;
                    if (WireMap.String(request, "token") != token || WireMap.String(request, "generation") != generation) return;
                    var id = WireMap.String(request, "id");
                    if (string.IsNullOrEmpty(id)) return;
                    AutoGameValue result;
                    switch (WireMap.String(request, "command"))
                    {
                        case "execute_start": result = StartExecution(request); break;
                        case "execute_wait": result = WaitExecution(request); break;
                        default: result = RunWork(request); break;
                    }
                    WireProtocol.WriteFrame(stream, result);
                }
                catch { }
            }
        }

        static AutoGameValue StartExecution(IDictionary<string, AutoGameValue> request)
        {
            var executionId = WireMap.String(request, "executionId");
            var assemblyPath = WireMap.String(request, "assembly");
            var entryType = WireMap.String(request, "entryType");
            if (string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(assemblyPath) || string.IsNullOrEmpty(entryType))
                return Failed("Invalid execution entry point.", "UnityExecutionEntryPointInvalid", executionId);
            Execution execution;
            lock (Sync)
            {
                if (Executions.TryGetValue(executionId, out execution)) return Snapshot(execution);
                PruneExecutions();
                if (!active || CountActiveExecutions() >= 2048)
                    return Failed("The execution queue is unavailable.", null, executionId);
                execution = new Execution { Id = executionId, AssemblyPath = assemblyPath, EntryType = entryType,
                    Args = WireMap.Get(request, "args") };
                Executions.Add(executionId, execution);
                MainThreadActions.Enqueue(() => BeginExecution(execution));
            }
            return Snapshot(execution);
        }

        static AutoGameValue WaitExecution(IDictionary<string, AutoGameValue> request)
        {
            var executionId = WireMap.String(request, "executionId");
            Execution execution;
            lock (Sync)
            {
                if (string.IsNullOrEmpty(executionId) || !Executions.TryGetValue(executionId, out execution))
                    return WireMap.Object("state", "lost", "generation", generation, "executionId", executionId);
                if (WireMap.Boolean(request, "terminate")) CancelExecution(execution);
                execution.Changed.Reset();
            }
            var waitMs = Math.Max(0, Math.Min(30000, (int)WireMap.Int64(request, "waitMs", 0)));
            var deadline = Environment.TickCount + waitMs;
            while (!IsTerminal(execution.State) && waitMs > 0)
            {
                execution.Changed.Wait(waitMs);
                execution.Changed.Reset();
                waitMs = Math.Max(0, deadline - Environment.TickCount);
            }
            return Snapshot(execution);
        }

        static AutoGameValue RunWork(IDictionary<string, AutoGameValue> request)
        {
            var id = WireMap.String(request, "id");
            Work work;
            lock (Sync)
            {
                if (!Requests.TryGetValue(id, out work))
                {
                    if (!active || Queue.Count >= 2048) return Failed("The bridge queue is unavailable.", null, null);
                    work = new Work { Request = request };
                    Requests.Add(id, work);
                    Queue.Enqueue(work);
                }
            }
            work.Done.Wait(10000);
            lock (Sync)
            {
                if (work.State == "queued") work.State = "cancelled";
                return work.Result ?? WireMap.Object("state", work.State == "running" ? "unknown" : work.State, "generation", generation);
            }
        }

        public static void PumpSafely()
        {
            if (Interlocked.Exchange(ref cleanupPending, 0) != 0)
            {
                CleanupUnityState();
                return;
            }
            if (!active) return;
            try { Pump(); }
            catch (Exception error) { Fail("UnityBridgePumpFailed", error); }
        }

        static void Pump()
        {
            if (Time.realtimeSinceStartup >= nextLeaseCheck) { PruneClients(); nextLeaseCheck = Time.realtimeSinceStartup + 5; }
            Scheduler.Pump();
            Action[] actions;
            lock (Sync) { actions = MainThreadActions.ToArray(); MainThreadActions.Clear(); }
            for (var index = 0; index < actions.Length; index++)
            {
                try { actions[index](); }
                catch (Exception error) { Fail("UnityBridgeActionFailed", error); return; }
            }
            PumpExecutionCompletions();
            Work work = null;
            lock (Sync)
            {
                if (Queue.Count > 0) { work = Queue.Dequeue(); if (work.State == "queued") work.State = "running"; }
            }
            if (work != null) CompleteWork(work);
        }

        static void CompleteWork(Work work)
        {
            if (work.State != "running") { CompleteWorkState(work); return; }
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Main thread changed.");
                AutoGameValue result;
                switch (WireMap.String(work.Request, "command"))
                {
                    case "metadata": result = Metadata(); break;
                    case "lease": AddLease(work.Request); result = WireMap.Object("attached", true); break;
                    case "detach": RemoveLease(work.Request); result = WireMap.Object("detached", true); break;
                    case "stop": result = AutoGameValue.FromString("stopped"); Stop(); break;
                    default: throw new InvalidOperationException("Unknown command.");
                }
                work.Result = Completed(result);
            }
            catch (Exception error) { work.Result = Failed((error.InnerException ?? error).ToString(), null, null); }
            finally { CompleteWorkState(work); }
        }

        static void CompleteWorkState(Work work)
        {
            lock (Sync)
            {
                work.State = "completed";
                work.Done.Set();
                Requests.Remove(WireMap.String(work.Request, "id"));
            }
        }

        static AutoGameValue Metadata()
        {
            var references = new List<string>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                try { if (File.Exists(assemblies[index].Location) && !references.Contains(assemblies[index].Location)) references.Add(assemblies[index].Location); }
                catch { }
            }
            return WireMap.Object("version", Application.unityVersion, "dataPath", Application.dataPath,
                "executable", System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                "domain", AppDomain.CurrentDomain.FriendlyName, "mainThread", mainThread,
                "generation", generation, "bridgeSession", generation, "domainEpoch", GetDomainEpoch(),
                "runtimeIdentity", RuntimeIdentity.Value, "runtimeVersion", RuntimeIdentity.Version,
                "asyncExecution", true, "bridge", typeof(Bridge).Assembly.Location,
                "references", WireMap.Array(references));
        }

        static void BeginExecution(Execution execution)
        {
            lock (Sync)
            {
                if (execution.State != "queued") return;
                if (execution.CancellationRequested) { CompleteExecution(execution, "cancelled", null, null, null); return; }
                execution.State = "running";
                execution.Changed.Set();
            }
            BeginExecutionRuntime();
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Main thread changed.");
                var assembly = Assembly.LoadFrom(execution.AssemblyPath);
                var type = assembly.GetType(execution.EntryType, false);
                if (type == null) throw new ExecutionEntryPointException("The compiled Unity entry type was not found.");
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(AutoGameArgs), typeof(UnityExecutionContext), typeof(CancellationToken) }, null);
                if (method == null || method.ReturnType != typeof(Task<object>))
                    throw new ExecutionEntryPointException("The compiled Unity entry point has an invalid signature.");
                var context = new UnityExecutionContext(execution.Cancellation.Token, ScheduleContinuation);
                execution.Task = (Task<object>)method.Invoke(null,
                    new object[] { new AutoGameArgs(execution.Args), context, execution.Cancellation.Token });
            }
            catch (ExecutionEntryPointException error) { CompleteExecution(execution, "failed", null, error.Message, "UnityExecutionEntryPointInvalid"); }
            catch (Exception error) { CompleteExecution(execution, "failed", null, (error.InnerException ?? error).ToString(), null); }
        }

        static void PumpExecutionCompletions()
        {
            var completed = new List<Execution>();
            lock (Sync)
                foreach (var execution in Executions.Values)
                    if (execution.State == "running" && execution.Task != null && execution.Task.IsCompleted) completed.Add(execution);
            for (var index = 0; index < completed.Count; index++)
            {
                var execution = completed[index];
                if (execution.Task.IsCanceled || execution.Cancellation.IsCancellationRequested)
                    CompleteExecution(execution, "cancelled", null, null, null);
                else if (execution.Task.IsFaulted)
                    CompleteExecution(execution, "failed", null, (execution.Task.Exception.InnerException ?? execution.Task.Exception).ToString(), null);
                else CompleteExecution(execution, "completed", execution.Task.Result, null, null);
            }
        }

        static void CompleteExecution(Execution execution, string state, object result, string error, string errorCode)
        {
            AutoGameValue normalized = null;
            if (state == "completed" && result != null)
            {
                try
                {
                    normalized = UnityValueNormalizer.Normalize(result);
                    WireProtocol.Encode(normalized);
                }
                catch (Exception failure) { state = "failed"; error = failure.ToString(); }
            }
            lock (Sync)
            {
                execution.State = state; execution.Result = normalized; execution.Error = error; execution.ErrorCode = errorCode;
                execution.Changed.Set();
            }
            EndExecutionRuntime();
        }

        static void CancelExecution(Execution execution)
        {
            if (execution.CancellationRequested || IsTerminal(execution.State)) return;
            execution.CancellationRequested = true;
            if (Thread.CurrentThread.ManagedThreadId == mainThread) execution.Cancellation.Cancel();
            else lock (Sync) MainThreadActions.Enqueue(() => execution.Cancellation.Cancel());
            if (execution.State == "queued") execution.State = "cancelled";
            execution.Changed.Set();
        }

        static AutoGameValue Snapshot(Execution execution)
        {
            lock (Sync)
                return WireMap.Object("state", execution.State, "generation", generation,
                    "executionId", execution.Id, "elapsedMs", Math.Max(0, (long)(DateTime.UtcNow - execution.StartedUtc).TotalMilliseconds),
                    "cancellationRequested", execution.CancellationRequested, "errorCode", execution.ErrorCode,
                    "error", execution.Error, "result", execution.State == "completed" ? execution.Result : null);
        }

        static AutoGameValue Completed(AutoGameValue result) { return WireMap.Object("state", "completed", "generation", generation, "result", result); }
        static AutoGameValue Failed(string error, string code, string executionId)
        {
            return WireMap.Object("state", "failed", "generation", generation, "executionId", executionId, "errorCode", code, "error", error);
        }
        static bool IsTerminal(string state) { return state == "completed" || state == "failed" || state == "cancelled" || state == "lost"; }

        static void BeginExecutionRuntime()
        {
            if (activeExecutionCount++ != 0) return;
            savedRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
        }

        static void EndExecutionRuntime()
        {
            if (activeExecutionCount > 0) activeExecutionCount--;
            if (activeExecutionCount == 0) Application.runInBackground = savedRunInBackground;
        }

        internal static void ScheduleContinuation(UnityExecutionContext context, Action continuation, int frames, double seconds, Func<bool> predicate)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Unity continuation scheduling requires the Player main thread.");
            Scheduler.Schedule(context, continuation, frames, seconds, predicate);
        }

        static void AddLease(IDictionary<string, AutoGameValue> request)
        {
            Clients[WireMap.String(request, "clientId")] = new ClientLease
            {
                Pid = checked((int)WireMap.Int64(request, "hostPid", 0)),
                StartUtc = DateTime.Parse(WireMap.String(request, "hostStartUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind)
            };
            SaveClients();
        }

        static void RemoveLease(IDictionary<string, AutoGameValue> request)
        {
            Clients.Remove(WireMap.String(request, "clientId"));
            SaveClients();
        }

        static void LoadClients()
        {
            Clients.Clear();
            if (!File.Exists(leasePath)) return;
            try
            {
                var map = WireProtocol.Decode(File.ReadAllBytes(leasePath)).AsObject();
                foreach (var pair in map)
                {
                    var lease = pair.Value.AsObject();
                    Clients[pair.Key] = new ClientLease { Pid = checked((int)WireMap.Int64(lease, "pid", 0)),
                        StartUtc = DateTime.Parse(WireMap.String(lease, "startUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind) };
                }
            }
            catch { Clients.Clear(); }
        }

        static void SaveClients()
        {
            var map = new Dictionary<string, AutoGameValue>(StringComparer.Ordinal);
            foreach (var pair in Clients) map[pair.Key] = WireMap.Object("pid", pair.Value.Pid, "startUtc", pair.Value.StartUtc.ToString("O"));
            File.WriteAllBytes(leasePath, WireProtocol.Encode(AutoGameValue.FromObject(map)));
        }

        static void PruneClients()
        {
            var expired = new List<string>();
            foreach (var pair in Clients)
            {
                try
                {
                    using (var process = System.Diagnostics.Process.GetProcessById(pair.Value.Pid))
                        if (!process.HasExited && process.StartTime.ToUniversalTime() == pair.Value.StartUtc) continue;
                }
                catch { }
                expired.Add(pair.Key);
            }
            if (expired.Count == 0) return;
            for (var index = 0; index < expired.Count; index++) Clients.Remove(expired[index]);
            SaveClients();
        }

        static void PruneExecutions()
        {
            var expired = new List<string>();
            foreach (var pair in Executions)
                if (IsTerminal(pair.Value.State) && (DateTime.UtcNow - pair.Value.StartedUtc).TotalMinutes > 10) expired.Add(pair.Key);
            for (var index = 0; index < expired.Count; index++) Executions.Remove(expired[index]);
        }

        static int CountActiveExecutions()
        {
            var count = 0;
            foreach (var execution in Executions.Values) if (!IsTerminal(execution.State)) count++;
            return count;
        }

        static void ResetState()
        {
            lock (Sync)
            {
                Queue.Clear(); MainThreadActions.Clear(); Requests.Clear(); Executions.Clear(); Scheduler.Invalidate();
            }
            activeExecutionCount = 0;
            nextLeaseCheck = 0;
            Interlocked.Exchange(ref cleanupPending, 0);
        }

        static void Fail(string code, Exception error)
        {
            if (Interlocked.Exchange(ref failing, 1) != 0) return;
            active = false;
            try { if (listener != null) listener.Stop(); } catch { }
            if (mainThread != 0 && Thread.CurrentThread.ManagedThreadId == mainThread) CleanupUnityState();
            else Interlocked.Exchange(ref cleanupPending, 1);
            lock (Sync)
            {
                foreach (var execution in Executions.Values) execution.Changed.Set();
                foreach (var work in Queue) { work.State = "cancelled"; work.Done.Set(); }
                Queue.Clear(); MainThreadActions.Clear(); Scheduler.Invalidate();
            }
            try { if (!string.IsNullOrEmpty(connectionPath) && File.Exists(connectionPath)) File.Delete(connectionPath); } catch { }
            WriteError(code, error);
        }

        static void WriteError(string code, Exception error)
        {
            if (string.IsNullOrEmpty(connectionPath)) return;
            try
            {
                var value = WireMap.Object("code", code, "message", error == null ? code : error.ToString());
                File.WriteAllBytes(connectionPath + ".error", WireProtocol.Encode(value));
            }
            catch { }
        }

        public static void Stop()
        {
            active = false;
            try { if (listener != null) listener.Stop(); } catch { }
            CleanupUnityState();
            lock (Sync)
            {
                foreach (var execution in Executions.Values) CancelExecution(execution);
                foreach (var work in Queue) { work.State = "cancelled"; work.Done.Set(); }
                Queue.Clear(); MainThreadActions.Clear(); Scheduler.Invalidate();
            }
            try { if (File.Exists(connectionPath)) File.Delete(connectionPath); } catch { }
        }

        static void CleanupUnityState()
        {
            try { if (pump != null) pump.enabled = false; } catch { }
            try { if (pumpObject != null) UnityEngine.Object.Destroy(pumpObject); } catch { }
            try { if (activeExecutionCount > 0) Application.runInBackground = savedRunInBackground; } catch { }
            activeExecutionCount = 0;
            pump = null;
            pumpObject = null;
        }

        sealed class ExecutionEntryPointException : Exception
        {
            public ExecutionEntryPointException(string message) : base(message) { }
        }
    }
}
