using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace AutoGame.Decompiler;

public sealed record DecompileResult(string Assembly, string OutputDirectory, int SourceFiles);

public static class GameDecompiler
{
    public static IReadOnlyList<DecompileResult> Decompile(string managedPath, string outputPath, string? assemblyName, bool all)
    {
        if (!Directory.Exists(managedPath)) throw new DirectoryNotFoundException(managedPath);
        if (!all && string.IsNullOrWhiteSpace(assemblyName))
            throw new ArgumentException("Specify --assembly <name.dll> or pass --all explicitly.");
        var assemblies = all
            ? Directory.GetFiles(managedPath, "*.dll").Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { ResolveAssembly(managedPath, assemblyName!) };
        Directory.CreateDirectory(outputPath);
        var results = new List<DecompileResult>();
        foreach (var assembly in assemblies)
        {
            var assemblyOutput = Path.Combine(outputPath, Path.GetFileNameWithoutExtension(assembly));
            Directory.CreateDirectory(assemblyOutput);
            var decompiler = new CSharpDecompiler(assembly, new DecompilerSettings(LanguageVersion.Latest));
            var count = 0;
            foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions.Where(type => type.DeclaringTypeDefinition is null))
            {
                if (type.Name == "<Module>") continue;
                var namespacePath = string.IsNullOrEmpty(type.Namespace)
                    ? assemblyOutput
                    : type.Namespace.Split('.').Select(SafeName).Aggregate(assemblyOutput, Path.Combine);
                Directory.CreateDirectory(namespacePath);
                var file = Path.Combine(namespacePath, SafeName(type.Name) + ".cs");
                if (File.Exists(file)) file = Path.Combine(namespacePath, SafeName(type.Name) + "_" + count + ".cs");
                File.WriteAllText(file, decompiler.DecompileTypeAsString(type.FullTypeName));
                count++;
            }
            results.Add(new DecompileResult(Path.GetFileName(assembly), assemblyOutput, count));
        }
        return results;
    }

    private static string ResolveAssembly(string managedPath, string name)
    {
        var candidate = Path.Combine(managedPath, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
        if (!File.Exists(candidate)) throw new FileNotFoundException("Managed assembly not found.", candidate);
        return candidate;
    }

    private static string SafeName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }
}
