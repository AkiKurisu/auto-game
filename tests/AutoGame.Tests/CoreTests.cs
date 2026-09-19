using AutoGame;
using AutoGame.Decompiler;
using System.Reflection.Metadata;
using Xunit;

namespace AutoGame.Tests;

public sealed class CoreTests
{
    [Fact]
    public void AttachDistributionEmbedsNativeRuntime()
    {
        var resources = typeof(PlayerAttachService).Assembly.GetManifestResourceNames();
        Assert.Contains("AutoGame.Native.dll", resources);
        Assert.DoesNotContain(resources, name => name.EndsWith("Newtonsoft.Json.dll", StringComparison.Ordinal));
        Assert.Contains("AutoGame.Payload.AutoGameValue.cs", resources);
        Assert.Contains("AutoGame.Payload.WireProtocol.cs", resources);
    }

    [Fact]
    public void SnippetBuilderDoesNotImportEditorApis()
    {
        var source = SnippetSourceBuilder.Build("Snippet", "return Application.unityVersion;", "test.cs");
        Assert.Contains("using UnityEngine;", source);
        Assert.Contains("Run(AutoGameArgs Args", source);
        Assert.DoesNotContain("Newtonsoft", source);
        Assert.DoesNotContain("UnityEditor", source);
    }

    [Fact]
    public void DecompilerCreatesSourceTree()
    {
        var output = Path.Combine(Path.GetTempPath(), "auto-game-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assembly = typeof(CoreTests).Assembly.Location;
            var result = GameDecompiler.Decompile(Path.GetDirectoryName(assembly)!, output, Path.GetFileName(assembly), false);
            Assert.Single(result);
            Assert.True(result[0].SourceFiles > 0);
            Assert.NotEmpty(Directory.GetFiles(result[0].OutputDirectory, "*.cs", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    public void PayloadCompilesAgainstConfiguredUnityManagedProfile()
    {
        var managedPath = Environment.GetEnvironmentVariable("AUTOGAME_TEST_MANAGED");
        if (string.IsNullOrEmpty(managedPath)) return;
        var root = Path.Combine(Path.GetTempPath(), "auto-game-payload-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        try
        {
            Directory.CreateDirectory(source);
            var owner = typeof(PlayerAttachService).Assembly;
            foreach (var name in owner.GetManifestResourceNames().Where(name => name.EndsWith(".cs", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(owner.GetManifestResourceStream(name)!);
                File.WriteAllText(Path.Combine(source, name), reader.ReadToEnd());
            }
            File.WriteAllText(Path.Combine(source, "RuntimeIdentity.cs"),
                "namespace AutoGame { internal static class RuntimeIdentity { public const string Value = \"test\"; public const string Version = \"test\"; } }");
            TargetCapabilityValidator.Validate(managedPath);
            var references = Directory.GetFiles(managedPath, "*.dll")
                .Where(file => Path.GetFileName(file).StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).StartsWith("System", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var payload = TargetCompiler.Compile(source, references, Path.Combine(root, "cache"));
            Assert.True(File.Exists(payload));
            var assemblyReferences = ReadAssemblyReferences(payload);
            var targetAssemblies = references.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(assemblyReferences, reference => Assert.Contains(reference, targetAssemblies));
            Assert.DoesNotContain("Newtonsoft.Json", assemblyReferences);
            Assert.DoesNotContain("System.Numerics", assemblyReferences);
            Assert.DoesNotContain("Mono.Cecil", assemblyReferences);

            var scripts = Path.Combine(FindRepositoryRoot(), "plugins", "auto-game", "skills", "auto-game-unity", "scripts");
            targetAssemblies.Add(Path.GetFileNameWithoutExtension(payload));
            foreach (var script in Directory.GetFiles(scripts, "*.cs"))
            {
                var generated = Path.Combine(root, "snippet-" + Path.GetFileName(script));
                File.WriteAllText(generated, SnippetSourceBuilder.Build(
                    "Snippet", File.ReadAllText(script), Path.GetFileName(script)));
                var snippet = TargetCompiler.Compile(generated, references.Append(payload),
                    Path.Combine(root, "snippet-cache"), "Snippet_");
                Assert.True(File.Exists(snippet));
                Assert.All(ReadAssemblyReferences(snippet), reference => Assert.Contains(reference, targetAssemblies));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AutoGame.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("AutoGame repository root was not found.");
    }

    private static string[] ReadAssemblyReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToArray();
    }
}
