using System.Diagnostics;

namespace AutoGame;

internal sealed record PlayerTarget(
    int Pid,
    DateTime StartedUtc,
    string Executable,
    string DataPath,
    string ManagedPath,
    string MonoModule,
    string UnityPlayerModule);

internal static class PlayerDiscovery
{
    public static IEnumerable<PlayerTarget> List()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                PlayerTarget? target = null;
                try { target = Inspect(process); }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
                if (target is not null) yield return target;
            }
        }
    }

    public static PlayerTarget Get(int pid)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new PlayerTargetException("UnityTargetUnavailable", $"Process {pid} is not running."); }
        using (process)
        {
            try { return Inspect(process) ?? throw new PlayerTargetException("UnityTargetUnsupported", $"Process {pid} is not a supported Unity Mono Player."); }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new PlayerTargetException("UnityTargetInspectionFailed", $"Cannot inspect process {pid}: {error.Message}");
            }
        }
    }

    private static PlayerTarget? Inspect(Process process)
    {
        if (process.HasExited || process.MainModule?.FileName is not { } executable) return null;
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        var unity = modules.FirstOrDefault(module => module.ModuleName.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));
        var mono = modules.FirstOrDefault(module => module.ModuleName.Equals("mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase)
            || module.ModuleName.Equals("mono-2.0-sgen.dll", StringComparison.OrdinalIgnoreCase));
        if (unity is null || mono is null) return null;
        var directory = Path.GetDirectoryName(executable)!;
        var dataPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(executable) + "_Data");
        var managedPath = Path.Combine(dataPath, "Managed");
        if (!Directory.Exists(managedPath)) return null;
        return new PlayerTarget(process.Id, process.StartTime.ToUniversalTime(), executable, dataPath, managedPath, mono.FileName, unity.FileName);
    }
}
