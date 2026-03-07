using System.Linq;
using System.Text.Json;
using System.IO;
using DecompilerServer;
using DecompilerServer.Services;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

public class RegressionTests : ServiceTestBase
{
    private readonly DecompilerService _decompilerService;

    public RegressionTests()
    {
        var repoRoot = FindRepoRoot();
        var testAssemblyPath = Path.Combine(repoRoot, "TestLibrary", "bin", "Debug", "net8.0", "test.dll");
        var tempDir = Path.Combine(Path.GetTempPath(), $"DecompilerServerRegression_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        ContextManager.LoadAssembly(tempDir, testAssemblyPath);

        _decompilerService = new DecompilerService(ContextManager, MemberResolver);

        var services = new ServiceCollection();
        services.AddSingleton(ContextManager);
        services.AddSingleton(MemberResolver);
        services.AddSingleton(_decompilerService);
        services.AddSingleton(new UsageAnalyzer(ContextManager, MemberResolver));
        services.AddSingleton(new InheritanceAnalyzer(ContextManager, MemberResolver));
        services.AddSingleton(new ResponseFormatter());
        ServiceLocator.SetServiceProvider(services.BuildServiceProvider());
    }

    [Fact]
    public void DecompileGenericMethodMember_ShouldReturnGenericDeclaringTypeSource()
    {
        var genericType = GetUniqueTypeByName("GenericClass");
        var method = genericType.Methods.Single(m => m.Name == "GenericMethod");

        var document = _decompilerService.DecompileMember(MemberResolver.GenerateMemberId(method));
        var source = string.Join("\n", document.Lines);

        Assert.Contains("class GenericClass<T>", source);
        Assert.Contains("GenericMethod(T parameter)", source);
    }

    [Fact]
    public void DecompileGenericFieldMember_ShouldReturnGenericDeclaringTypeSource()
    {
        var genericType = GetUniqueTypeByName("GenericClass");
        var field = genericType.Fields.Single(f => f.Name == "_value");

        var document = _decompilerService.DecompileMember(MemberResolver.GenerateMemberId(field));
        var source = string.Join("\n", document.Lines);

        Assert.Contains("class GenericClass<T>", source);
        Assert.Contains("_value", source);
    }

    [Fact]
    public void DecompileGenericPropertyMember_ShouldReturnGenericDeclaringTypeSource()
    {
        var genericType = GetUniqueTypeByName("GenericClass");
        var property = genericType.Properties.Single(p => p.Name == "Current");

        var document = _decompilerService.DecompileMember(MemberResolver.GenerateMemberId(property));
        var source = string.Join("\n", document.Lines);

        Assert.Contains("class GenericClass<T>", source);
        Assert.Contains("Current", source);
    }

    [Fact]
    public void DecompileGenericEventMember_ShouldReturnGenericDeclaringTypeSource()
    {
        var genericType = GetUniqueTypeByName("GenericClass");
        var evt = genericType.Events.Single(e => e.Name == "Changed");

        var document = _decompilerService.DecompileMember(MemberResolver.GenerateMemberId(evt));
        var source = string.Join("\n", document.Lines);

        Assert.Contains("class GenericClass<T>", source);
        Assert.Contains("Changed", source);
    }

    [Fact]
    public void DecompileGenericAliasMember_ShouldNotResolveToNonGenericSibling()
    {
        var genericAliasType = GetTypeByDecompiledDeclaration("AliasClass", "class AliasClass<T>");
        var method = genericAliasType.Methods.Single(m => m.Name == "Set");

        var document = _decompilerService.DecompileMember(MemberResolver.GenerateMemberId(method));
        var source = string.Join("\n", document.Lines);

        Assert.Contains("class AliasClass<T>", source);
        Assert.DoesNotContain("class AliasClass\n", source);
    }

    [Fact]
    public void SetDecompileSettings_ShouldApplyBooleanValues()
    {
        var original = ContextManager.GetCurrentSettings();

        try
        {
            var initial = ParseToolResult(SetDecompileSettingsTool.SetDecompileSettings(new Dictionary<string, object>()));

            Assert.True(initial.GetProperty("usingDeclarations").GetBoolean());
            Assert.True(initial.GetProperty("removeDeadCode").GetBoolean());
            Assert.True(initial.GetProperty("alwaysUseBraces").GetBoolean());

            var updated = ParseToolResult(SetDecompileSettingsTool.SetDecompileSettings(new Dictionary<string, object>
            {
                ["usingDeclarations"] = false,
                ["removeDeadCode"] = false,
                ["alwaysUseBraces"] = false
            }));

            Assert.False(updated.GetProperty("usingDeclarations").GetBoolean());
            Assert.False(updated.GetProperty("removeDeadCode").GetBoolean());
            Assert.False(updated.GetProperty("alwaysUseBraces").GetBoolean());

            Assert.False(ContextManager.GetCurrentSettings().UsingDeclarations);
            Assert.False(ContextManager.GetCurrentSettings().RemoveDeadCode);
            Assert.False(ContextManager.GetCurrentSettings().AlwaysUseBraces);
        }
        finally
        {
            ContextManager.UpdateSettings(original);
            _decompilerService.ClearCache();
        }
    }

    [Fact]
    public void SetDecompileSettings_ShouldHandleJsonElementBooleans_AndStatusShouldReflectThem()
    {
        var original = ContextManager.GetCurrentSettings();

        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(
                """
                {
                  "usingDeclarations": false,
                  "removeDeadCode": false,
                  "alwaysUseBraces": false,
                  "makeAssignmentExpressions": false
                }
                """
            )!;

            var updated = ParseToolResult(SetDecompileSettingsTool.SetDecompileSettings(payload));

            Assert.False(updated.GetProperty("usingDeclarations").GetBoolean());
            Assert.False(updated.GetProperty("removeDeadCode").GetBoolean());
            Assert.False(updated.GetProperty("alwaysUseBraces").GetBoolean());
            Assert.False(updated.GetProperty("makeAssignmentExpressions").GetBoolean());

            var statusRoot = JsonSerializer.Deserialize<JsonElement>(StatusTool.Status());
            Assert.Equal("ok", statusRoot.GetProperty("status").GetString());
            var settings = statusRoot.GetProperty("data").GetProperty("settings");

            Assert.False(settings.GetProperty("usingDeclarations").GetBoolean());
            Assert.False(settings.GetProperty("removeDeadCode").GetBoolean());
            Assert.False(settings.GetProperty("alwaysUseBraces").GetBoolean());
            Assert.False(settings.GetProperty("makeAssignmentExpressions").GetBoolean());

            Assert.False(ContextManager.GetCurrentSettings().UsingDeclarations);
            Assert.False(ContextManager.GetCurrentSettings().RemoveDeadCode);
            Assert.False(ContextManager.GetCurrentSettings().AlwaysUseBraces);
            Assert.False(ContextManager.GetCurrentSettings().MakeAssignmentExpressions);
        }
        finally
        {
            ContextManager.UpdateSettings(original);
            _decompilerService.ClearCache();
        }
    }

    private ITypeDefinition GetUniqueTypeByName(string name)
    {
        return ContextManager.GetAllTypes().Single(t => t.Name == name);
    }

    private ITypeDefinition GetTypeByDecompiledDeclaration(string name, string declaration)
    {
        return ContextManager.GetAllTypes()
            .Where(t => t.Name == name)
            .Single(t => string.Join("\n", _decompilerService.DecompileMember(MemberResolver.GenerateMemberId(t)).Lines).Contains(declaration));
    }

    private static JsonElement ParseToolResult(string result)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.True(root.GetProperty("status").GetString() == "ok", result);
        return root.GetProperty("data");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DecompilerServer.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DecompilerServer.sln from test base directory.");
    }
}
