using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AutoGame;

internal static class TargetCapabilityValidator
{
    internal static void Validate(string managedPath)
    {
        var assemblies = Directory.GetFiles(managedPath, "*.dll")
            .Where(path => Path.GetFileName(path).StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var types = ReadTypes(assemblies);
        Require(types, "System.Threading.Tasks.Task", null, "UnityTargetUnsupportedRuntimeProfile");
        Require(types, "System.Threading.ManualResetEventSlim", null, "UnityTargetUnsupportedRuntimeProfile");
        Require(types, "System.Reflection.Assembly", "LoadFrom", "UnityTargetUnsupportedRuntimeProfile");
        Require(types, "System.Linq.Enumerable", null, "UnityTargetUnsupportedRuntimeProfile");
        Require(types, "System.Net.Sockets.TcpListener", null, "UnityTargetUnsupportedNetworking");
        Require(types, "UnityEngine.Object", "DontDestroyOnLoad", "UnityTargetUnsupportedUnityApi");
        Require(types, "UnityEngine.MonoBehaviour", null, "UnityTargetUnsupportedUnityApi");
        Require(types, "UnityEngine.GameObject", "AddComponent", "UnityTargetUnsupportedUnityApi");
        Require(types, "UnityEngine.Camera", "onPreCull", "UnityTargetUnsupportedUnityApi");
        Require(types, "UnityEngine.Application", "onBeforeRender", "UnityTargetUnsupportedUnityApi");
        Require(types, "UnityEngine.Canvas", "willRenderCanvases", "UnityTargetUnsupportedUnityApi");
    }

    private static Dictionary<string, HashSet<string>> ReadTypes(IEnumerable<string> assemblies)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in assemblies)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var metadata = pe.GetMetadataReader();
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var definition = metadata.GetTypeDefinition(handle);
                    var name = metadata.GetString(definition.Name);
                    if (name == "<Module>" || definition.GetDeclaringType().IsNil == false) continue;
                    var ns = metadata.GetString(definition.Namespace);
                    var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    if (!result.TryGetValue(fullName, out var methods))
                        result.Add(fullName, methods = new HashSet<string>(StringComparer.Ordinal));
                    foreach (var methodHandle in definition.GetMethods())
                        methods.Add(metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name));
                    foreach (var eventHandle in definition.GetEvents())
                        methods.Add(metadata.GetString(metadata.GetEventDefinition(eventHandle).Name));
                    foreach (var fieldHandle in definition.GetFields())
                        methods.Add(metadata.GetString(metadata.GetFieldDefinition(fieldHandle).Name));
                }
            }
            catch (BadImageFormatException) { }
        }
        return result;
    }

    private static void Require(
        Dictionary<string, HashSet<string>> types,
        string type,
        string? method,
        string code)
    {
        if (types.TryGetValue(type, out var methods) && (method is null || methods.Contains(method))) return;
        var capability = method is null ? type : type + "." + method;
        throw new PlayerTargetException(code, "The Unity Player is missing required capability: " + capability + ". No injection was attempted.");
    }
}
