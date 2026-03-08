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
        var tempDir = Path.Combine(Path.GetTempPath(), $"DecompilerServerRegression_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        ContextManager.LoadAssembly(tempDir, TestAssemblyPath);

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

    // ───── Generic arity disambiguation tests ─────

    [Fact]
    public void FindTypeByName_BacktickArity_ResolvesCorrectGeneric()
    {
        var t0 = ContextManager.FindTypeByName("TestLibrary.ArityTest");
        var t1 = ContextManager.FindTypeByName("TestLibrary.ArityTest`1");
        var t2 = ContextManager.FindTypeByName("TestLibrary.ArityTest`2");

        Assert.NotNull(t0);
        Assert.NotNull(t1);
        Assert.NotNull(t2);
        Assert.Equal(0, t0!.TypeParameterCount);
        Assert.Equal(1, t1!.TypeParameterCount);
        Assert.Equal(2, t2!.TypeParameterCount);
        Assert.NotEqual(t0.MetadataToken, t1.MetadataToken);
        Assert.NotEqual(t1.MetadataToken, t2.MetadataToken);
    }

    [Fact]
    public void FindTypeByName_AliasClass_BacktickDisambiguates()
    {
        var nonGeneric = ContextManager.FindTypeByName("TestLibrary.AliasClass");
        var generic = ContextManager.FindTypeByName("TestLibrary.AliasClass`1");

        Assert.NotNull(nonGeneric);
        Assert.NotNull(generic);
        Assert.Equal(0, nonGeneric!.TypeParameterCount);
        Assert.Equal(1, generic!.TypeParameterCount);
    }

    [Fact]
    public void FindTypeByName_ShortBacktick_ResolvesGeneric()
    {
        // Short name with backtick (e.g. "ArityTest`2") should work
        var t2 = ContextManager.FindTypeByName("ArityTest`2");
        Assert.NotNull(t2);
        Assert.Equal(2, t2!.TypeParameterCount);
    }

    [Fact]
    public void FindTypeByName_UniqueSimpleName_StillWorks()
    {
        // GenericClass has no non-generic sibling, so simple name lookup should still work
        var gc = ContextManager.FindTypeByName("GenericClass");
        Assert.NotNull(gc);
        Assert.Equal(1, gc!.TypeParameterCount);
    }

    // ───── T:/M:/F:/P:/E: path resolution with generics ─────

    [Fact]
    public void ResolveMember_T_BacktickArity()
    {
        var entity = MemberResolver.ResolveMember("T:TestLibrary.ArityTest`1");
        Assert.NotNull(entity);
        Assert.IsAssignableFrom<ITypeDefinition>(entity);
        Assert.Equal(1, ((ITypeDefinition)entity!).TypeParameterCount);
    }

    [Fact]
    public void ResolveMember_T_UniqueGenericWithoutBacktick_ShouldResolve()
    {
        var entity = MemberResolver.ResolveMember("T:TestLibrary.GenericClass");
        Assert.NotNull(entity);
        Assert.IsAssignableFrom<ITypeDefinition>(entity);
        Assert.Equal(1, ((ITypeDefinition)entity!).TypeParameterCount);
    }

    [Fact]
    public void ResolveMember_M_OnGenericType_BacktickArity()
    {
        var entity = MemberResolver.ResolveMember("M:TestLibrary.ArityTest`1.Run");
        Assert.NotNull(entity);
        Assert.IsAssignableFrom<IMethod>(entity);
        Assert.Equal("Run", entity!.Name);
        Assert.Equal(1, entity.DeclaringTypeDefinition!.TypeParameterCount);
    }

    [Fact]
    public void ResolveMember_F_OnGenericType_BacktickArity()
    {
        var entity = MemberResolver.ResolveMember("F:TestLibrary.ArityTest`2.First");
        Assert.NotNull(entity);
        Assert.IsAssignableFrom<IField>(entity);
        Assert.Equal("First", entity!.Name);
        Assert.Equal(2, entity.DeclaringTypeDefinition!.TypeParameterCount);
    }

    // ───── Method overload disambiguation ─────

    [Fact]
    public void ResolveMember_M_OverloadByParamList_NoArgs()
    {
        var entity = MemberResolver.ResolveMember("M:TestLibrary.OverloadTest.Process()");
        Assert.NotNull(entity);
        var method = Assert.IsAssignableFrom<IMethod>(entity);
        Assert.Equal(0, method.Parameters.Count);
    }

    [Fact]
    public void ResolveMember_M_OverloadByParamList_OneArg()
    {
        var entity = MemberResolver.ResolveMember("M:TestLibrary.OverloadTest.Process(System.String)");
        Assert.NotNull(entity);
        var method = Assert.IsAssignableFrom<IMethod>(entity);
        Assert.Equal(1, method.Parameters.Count);
    }

    [Fact]
    public void ResolveMember_M_OverloadByParamList_TwoArgs()
    {
        var entity = MemberResolver.ResolveMember("M:TestLibrary.OverloadTest.Process(System.String,System.Int32)");
        Assert.NotNull(entity);
        var method = Assert.IsAssignableFrom<IMethod>(entity);
        Assert.Equal(2, method.Parameters.Count);
    }

    [Fact]
    public void ResolveMember_M_OverloadByGenericCollectionParamList()
    {
        var overloadType = ContextManager.FindTypeByName("TestLibrary.OverloadTest");
        Assert.NotNull(overloadType);
        Assert.Equal(2, overloadType!.Methods.Count(m => m.Name == "ConsumeCollection"));

        var entity = MemberResolver.ResolveMember(
            "M:TestLibrary.OverloadTest.ConsumeCollection(System.Collections.Generic.Dictionary{System.String,System.Int32})");
        Assert.NotNull(entity);

        var method = Assert.IsAssignableFrom<IMethod>(entity);
        Assert.Equal("ConsumeCollection", method.Name);
        Assert.Single(method.Parameters);

        var parameterType = method.Parameters[0].Type;
        var parameterTypeName = parameterType.ReflectionName ?? parameterType.FullName ?? parameterType.Name;
        Assert.Contains("Dictionary", parameterTypeName);
    }

    [Fact]
    public void ResolveMember_M_OverloadByParamList_NoMatch_ReturnsNull()
    {
        var entity = MemberResolver.ResolveMember("M:TestLibrary.OverloadTest.Process(System.Boolean)");
        Assert.Null(entity);
    }

    [Fact]
    public void ResolveMember_M_SingleCandidateWithWrongParamList_ReturnsNull()
    {
        var entity = MemberResolver.ResolveMember("M:TestLibrary.SimpleClass.SimpleMethod(System.Boolean)");
        Assert.Null(entity);
    }

    [Fact]
    public void ResolveMember_M_OverloadWithoutParens_FallsBackToFirst()
    {
        // Without param list, should still resolve (to first overload)
        var entity = MemberResolver.ResolveMember("M:TestLibrary.OverloadTest.Process");
        Assert.NotNull(entity);
        Assert.IsAssignableFrom<IMethod>(entity);
    }

    [Fact]
    public void ResolveMember_AfterAssemblyReload_ShouldRefreshCachedEntityForSameKey()
    {
        var beforeReload = MemberResolver.ResolveMember("M:TestLibrary.SimpleClass.SimpleMethod");
        Assert.NotNull(beforeReload);

        var tempDir = Path.Combine(Path.GetTempPath(), $"DecompilerServerRegressionReload_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        ContextManager.LoadAssembly(tempDir, TestAssemblyPath);

        var afterReload = MemberResolver.ResolveMember("M:TestLibrary.SimpleClass.SimpleMethod");
        Assert.NotNull(afterReload);
        Assert.IsAssignableFrom<IMethod>(afterReload);
        Assert.NotSame(beforeReload, afterReload);
    }

    [Fact]
    public void FindTypeByName_ShortBacktick_AmbiguousAcrossNamespaces_ReturnsNull()
    {
        var entity = ContextManager.FindTypeByName("Duplicated`1");
        Assert.Null(entity);
    }

    [Fact]
    public void FindTypeByName_FullBacktick_AmbiguousAcrossNamespaces_StillResolves()
    {
        var left = ContextManager.FindTypeByName("TestLibrary.NamespaceA.Duplicated`1");
        var right = ContextManager.FindTypeByName("TestLibrary.NamespaceB.Duplicated`1");

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.NotEqual(left!.MetadataToken, right!.MetadataToken);
    }

    [Fact]
    public void FindTypeByName_FullName_AmbiguousAcrossNamespaces_StillResolves()
    {
        var left = ContextManager.FindTypeByName("TestLibrary.NamespaceA.Duplicated");
        var right = ContextManager.FindTypeByName("TestLibrary.NamespaceB.Duplicated");

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(1, left!.TypeParameterCount);
        Assert.Equal(1, right!.TypeParameterCount);
        Assert.NotEqual(left.MetadataToken, right.MetadataToken);
    }

    [Fact]
    public void FindTypeByName_ShortBacktick_UniqueArity_StillWorks()
    {
        var entity = ContextManager.FindTypeByName("AliasClass`1");
        Assert.NotNull(entity);
        Assert.Equal(1, entity!.TypeParameterCount);
    }
}
