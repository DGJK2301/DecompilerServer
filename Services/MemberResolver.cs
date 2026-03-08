using ICSharpCode.Decompiler.TypeSystem;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System;

namespace DecompilerServer.Services;

/// <summary>
/// Resolves member IDs to IEntity objects and handles member ID normalization.
/// Supports various member ID formats and provides consistent resolution with caching.
/// </summary>
public class MemberResolver
{
    private readonly AssemblyContextManager _contextManager;
    private readonly ConcurrentDictionary<string, IEntity?> _resolutionCache = new();
    private readonly object _cacheScopeLock = new();
    private ICompilation? _lastCompilation;

    public MemberResolver(AssemblyContextManager contextManager)
    {
        _contextManager = contextManager;
    }

    /// <summary>
    /// Resolve a member ID to an IEntity (type, method, field, property, event) with caching
    /// </summary>
    public IEntity? ResolveMember(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return null;

        var compilation = _contextManager.GetCompilation();
        EnsureCompilationScope(compilation);

        // Check cache first
        if (_resolutionCache.TryGetValue(memberId, out var cached))
            return cached;

        // Try fast path with indexed members first
        var entity = _contextManager.FindMemberById(memberId) ??
                     ResolveMemberByFullName(memberId, compilation) ??
                     ResolveMemberByTokenId(memberId, compilation) ??
                     ResolveMemberByMetadataToken(memberId, compilation);

        // Cache the result (including null results to avoid repeated failed lookups)
        _resolutionCache.TryAdd(memberId, entity);

        return entity;
    }

    private void EnsureCompilationScope(ICompilation compilation)
    {
        lock (_cacheScopeLock)
        {
            if (!ReferenceEquals(_lastCompilation, compilation))
            {
                _resolutionCache.Clear();
                _lastCompilation = compilation;
            }
        }
    }

    /// <summary>
    /// Resolve a member ID specifically to a type
    /// </summary>
    public IType? ResolveType(string typeId)
    {
        var entity = ResolveMember(typeId);
        return entity as IType ?? (entity as IMember)?.DeclaringType;
    }

    /// <summary>
    /// Resolve a member ID specifically to a method
    /// </summary>
    public IMethod? ResolveMethod(string methodId)
    {
        return ResolveMember(methodId) as IMethod;
    }

    /// <summary>
    /// Resolve a member ID specifically to a field
    /// </summary>
    public IField? ResolveField(string fieldId)
    {
        return ResolveMember(fieldId) as IField;
    }

    /// <summary>
    /// Resolve a member ID specifically to a property
    /// </summary>
    public IProperty? ResolveProperty(string propertyId)
    {
        return ResolveMember(propertyId) as IProperty;
    }

    /// <summary>
    /// Normalize a member ID to a consistent format
    /// </summary>
    public string NormalizeMemberId(string memberId)
    {
        var entity = ResolveMember(memberId);
        if (entity == null)
            return memberId; // Return original if can't resolve

        return GenerateMemberId(entity);
    }

    /// <summary>
    /// Generate a consistent member ID from an IEntity
    /// </summary>
    public string GenerateMemberId(IEntity entity)
    {
        var mvid = _contextManager.Mvid;
        if (mvid == null)
        {
            mvid = new string('0', 32);
        }
        else if (mvid.Contains('-'))
        {
            mvid = Guid.Parse(mvid).ToString("N");
        }

        var token = MetadataTokens.GetToken(entity.MetadataToken);
        var kind = entity switch
        {
            ITypeDefinition => 'T',
            IMethod => 'M',
            IField => 'F',
            IProperty => 'P',
            IEvent => 'E',
            _ => 'T'
        };

        return $"{mvid}:{token:X8}:{kind}";
    }

    /// <summary>
    /// Get a human-readable signature for a member
    /// </summary>
    public string GetMemberSignature(IEntity entity)
    {
        return entity switch
        {
            IMethod method => FormatMethodSignature(method),
            IProperty property => FormatPropertySignature(property),
            IField field => FormatFieldSignature(field),
            IEvent evt => FormatEventSignature(evt),
            IType type => FormatTypeSignature(type),
            _ => entity.Name
        };
    }

    /// <summary>
    /// Check if a member ID is valid format
    /// </summary>
    public bool IsValidMemberId(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return false;

        return Regex.IsMatch(memberId,
            @"^([0-9A-Fa-f]{32}:[0-9A-Fa-f]{8}:[TMFPE]|[TMFPE]:.*|0x[0-9A-Fa-f]+|\d+)$");
    }

    /// <summary>
    /// Clear the resolution cache
    /// </summary>
    public void ClearCache()
    {
        _resolutionCache.Clear();
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public ResolverCacheStats GetCacheStats()
    {
        return new ResolverCacheStats(
            _resolutionCache.Count,
            _resolutionCache.Count(kv => kv.Value != null),
            _resolutionCache.Count(kv => kv.Value == null));
    }

    private IEntity? ResolveMemberByFullName(string memberId, ICompilation compilation)
    {
        // Handle XML documentation style IDs (T:, M:, F:, P:, E:)
        if (memberId.Length < 3 || memberId[1] != ':')
            return null;

        var prefix = memberId[0];
        var fullName = memberId.Substring(2);

        return prefix switch
        {
            'T' => ResolveTypeByFullName(fullName, compilation),
            'M' => FindMethodByFullName(fullName, compilation),
            'F' => FindFieldByFullName(fullName, compilation),
            'P' => FindPropertyByFullName(fullName, compilation),
            'E' => FindEventByFullName(fullName, compilation),
            _ => null
        };
    }

    /// <summary>
    /// Resolve a type by full name, trying the cached index first (handles backtick arity),
    /// then falling back to FullTypeName.
    /// </summary>
    private ITypeDefinition? ResolveTypeByFullName(string fullName, ICompilation compilation)
    {
        return _contextManager.FindTypeByName(fullName) ??
               compilation.FindType(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(fullName)).GetDefinition();
    }

    private IEntity? ResolveMemberByTokenId(string memberId, ICompilation compilation)
    {
        // Handle hex tokens like "0x06000123"
        if (!memberId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(memberId.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var token))
            return null;

        return ResolveMemberByToken(token, compilation);
    }

    private IEntity? ResolveMemberByMetadataToken(string memberId, ICompilation compilation)
    {
        // Handle decimal tokens
        if (!int.TryParse(memberId, out var token))
            return null;

        return ResolveMemberByToken(token, compilation);
    }

    private IEntity? ResolveMemberByToken(int token, ICompilation compilation)
    {
        try
        {
            var peFile = _contextManager.GetPEFile();
            _ = peFile.Metadata; // ensure metadata is loaded

            if (compilation.MainModule is not ICSharpCode.Decompiler.TypeSystem.MetadataModule module)
                return null;

            var handle = MetadataTokens.EntityHandle(token);
            return module.ResolveEntity(handle);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Split "Namespace.Type.Member" or "Namespace.Type`1.Member" into (typeName, memberName).
    /// If input contains parentheses (param list), strip them first and return separately.
    /// </summary>
    private static (string typeName, string memberName, string? paramList) SplitTypeMember(string fullName)
    {
        string? paramList = null;
        var parenIdx = fullName.IndexOf('(');
        if (parenIdx >= 0)
        {
            paramList = fullName.Substring(parenIdx);
            fullName = fullName.Substring(0, parenIdx);
        }

        var lastDot = fullName.LastIndexOf('.');
        if (lastDot < 0)
            return ("", fullName, paramList);

        return (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1), paramList);
    }

    /// <summary>
    /// Try to resolve a type name via cached index then FullTypeName fallback.
    /// </summary>
    private ITypeDefinition? LookupType(string typeName, ICompilation compilation)
    {
        return _contextManager.FindTypeByName(typeName) ??
               compilation.FindType(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(typeName)).GetDefinition();
    }

    private IMethod? FindMethodByFullName(string fullName, ICompilation compilation)
    {
        var (typeName, methodName, paramList) = SplitTypeMember(fullName);
        if (string.IsNullOrEmpty(typeName)) return null;

        var type = LookupType(typeName, compilation);
        if (type == null) return null;

        var candidates = type.Methods.Where(m => m.Name == methodName).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Multiple overloads — try parameter list disambiguation
        if (paramList != null)
        {
            var paramTypes = ParseXmlDocParamList(paramList);
            var match = candidates.FirstOrDefault(m => MatchesParamTypes(m, paramTypes));
            if (match != null) return match;
        }

        // Fallback: return first overload
        return candidates[0];
    }

    /// <summary>
    /// Parse XML-doc style parameter list "(System.String,System.Int32)" into type name array.
    /// </summary>
    private static string[] ParseXmlDocParamList(string paramList)
    {
        var inner = paramList.TrimStart('(').TrimEnd(')').Trim();
        if (string.IsNullOrEmpty(inner)) return Array.Empty<string>();

        var parts = new List<string>();
        var start = 0;
        var genericDepth = 0;

        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '{':
                    genericDepth++;
                    break;
                case '}':
                    if (genericDepth > 0)
                        genericDepth--;
                    break;
                case ',':
                    if (genericDepth == 0)
                    {
                        parts.Add(inner.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                    break;
            }
        }

        parts.Add(inner.Substring(start).Trim());
        return parts.Where(static p => !string.IsNullOrEmpty(p)).ToArray();
    }

    /// <summary>
    /// Check if a method's parameter types match the given XML-doc type names.
    /// Matches by full name or simple name.
    /// </summary>
    private static bool MatchesParamTypes(IMethod method, string[] paramTypes)
    {
        if (method.Parameters.Count != paramTypes.Length) return false;
        for (int i = 0; i < paramTypes.Length; i++)
        {
            var pType = method.Parameters[i].Type;
            var expected = paramTypes[i];
            var normalizedExpected = NormalizeXmlDocTypeName(expected);
            var definition = pType.GetDefinition();
            var normalizedActual = NormalizeReflectionTypeName(pType.ReflectionName);

            if (pType.FullName != expected &&
                pType.ReflectionName != expected &&
                pType.Name != expected &&
                normalizedActual != normalizedExpected &&
                definition?.FullName != normalizedExpected &&
                definition?.ReflectionName != normalizedExpected &&
                definition?.Name != normalizedExpected)
                return false;
        }
        return true;
    }

    private static string NormalizeXmlDocTypeName(string typeName)
    {
        var trimmed = typeName.Trim();
        var genericStart = trimmed.IndexOf('{');
        if (genericStart < 0)
            return trimmed;

        var genericEnd = trimmed.LastIndexOf('}');
        if (genericEnd <= genericStart)
            return trimmed;

        var arity = CountTopLevelGenericArguments(trimmed.Substring(genericStart + 1, genericEnd - genericStart - 1));
        return $"{trimmed.Substring(0, genericStart)}`{arity}";
    }

    private static string? NormalizeReflectionTypeName(string? reflectionName)
    {
        if (string.IsNullOrWhiteSpace(reflectionName))
            return reflectionName;

        var genericInstanceStart = reflectionName.IndexOf("[[", StringComparison.Ordinal);
        if (genericInstanceStart < 0)
            return reflectionName;

        return reflectionName.Substring(0, genericInstanceStart);
    }

    private static int CountTopLevelGenericArguments(string genericArgs)
    {
        if (string.IsNullOrWhiteSpace(genericArgs))
            return 0;

        var count = 1;
        var genericDepth = 0;

        foreach (var ch in genericArgs)
        {
            switch (ch)
            {
                case '{':
                    genericDepth++;
                    break;
                case '}':
                    if (genericDepth > 0)
                        genericDepth--;
                    break;
                case ',':
                    if (genericDepth == 0)
                        count++;
                    break;
            }
        }

        return count;
    }

    private IField? FindFieldByFullName(string fullName, ICompilation compilation)
    {
        var (typeName, fieldName, _) = SplitTypeMember(fullName);
        if (string.IsNullOrEmpty(typeName)) return null;

        var type = LookupType(typeName, compilation);
        return type?.Fields.FirstOrDefault(f => f.Name == fieldName);
    }

    private IProperty? FindPropertyByFullName(string fullName, ICompilation compilation)
    {
        var (typeName, propertyName, _) = SplitTypeMember(fullName);
        if (string.IsNullOrEmpty(typeName)) return null;

        var type = LookupType(typeName, compilation);
        return type?.Properties.FirstOrDefault(p => p.Name == propertyName);
    }

    private IEvent? FindEventByFullName(string fullName, ICompilation compilation)
    {
        var (typeName, eventName, _) = SplitTypeMember(fullName);
        if (string.IsNullOrEmpty(typeName)) return null;

        var type = LookupType(typeName, compilation);
        return type?.Events.FirstOrDefault(e => e.Name == eventName);
    }

    private string FormatMethodSignature(IMethod method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
        return $"{method.ReturnType.Name} {method.Name}({parameters})";
    }

    private string FormatPropertySignature(IProperty property)
    {
        return $"{property.ReturnType.Name} {property.Name}";
    }

    private string FormatFieldSignature(IField field)
    {
        return $"{field.ReturnType.Name} {field.Name}";
    }

    private string FormatEventSignature(IEvent evt)
    {
        return $"event {evt.ReturnType.Name} {evt.Name}";
    }

    private string FormatTypeSignature(IType type)
    {
        return type.FullName;
    }
}

/// <summary>
/// Resolver cache statistics
/// </summary>
public record ResolverCacheStats(int CachedResolutions, int SuccessfulResolutions, int FailedResolutions);
