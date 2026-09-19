using AutoGame;
using Xunit;

namespace AutoGame.Tests;

public sealed class TargetCapabilityValidatorTests
{
    private const string CoreUnitySource = """
        using System;
        namespace UnityEngine {
          public class Object { public static void DontDestroyOnLoad(Object value) {} }
          public class MonoBehaviour : Object {}
          public class GameObject : Object { public object AddComponent(Type type) { return null; } }
          public class Camera : Object { public static object onPreCull; }
          public static class Application { public static object onBeforeRender; }
        }
        """;
    private const string UiUnitySource = """
        namespace UnityEngine { public class Canvas { public static object willRenderCanvases; } }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsMonolithicAndModularUnityLayouts(bool modular)
    {
        var root = CreateFixture(modular, includeUi: true);
        try { TargetCapabilityValidator.Validate(root); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsMissingUnityCapabilityBeforeInjection()
    {
        var root = CreateFixture(modular: true, includeUi: false);
        try
        {
            var error = Assert.Throws<PlayerTargetException>(() => TargetCapabilityValidator.Validate(root));
            Assert.Equal("UnityTargetUnsupportedUnityApi", error.Code);
            Assert.Contains("UnityEngine.Canvas", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateFixture(bool modular, bool includeUi)
    {
        var root = Path.Combine(Path.GetTempPath(), "auto-game-capabilities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(File.Exists)
            .ToArray();
        foreach (var reference in references.Where(path => Path.GetFileName(path).StartsWith("System", StringComparison.OrdinalIgnoreCase)))
            File.Copy(reference, Path.Combine(root, Path.GetFileName(reference)), true);

        if (modular)
        {
            CompileFixture(root, references, CoreUnitySource, "UnityEngine.CoreModule.dll", "Core");
            if (includeUi) CompileFixture(root, references, UiUnitySource, "UnityEngine.UIModule.dll", "UI");
        }
        else
        {
            CompileFixture(root, references, CoreUnitySource + (includeUi ? UiUnitySource : string.Empty), "UnityEngine.dll", "Unity");
        }
        return root;
    }

    private static void CompileFixture(string root, string[] references, string source, string fileName, string prefix)
    {
        var sourcePath = Path.Combine(root, prefix + ".cs");
        File.WriteAllText(sourcePath, source);
        var compiled = TargetCompiler.Compile(sourcePath, references, Path.Combine(root, "cache"), prefix);
        File.Copy(compiled, Path.Combine(root, fileName), true);
    }
}
