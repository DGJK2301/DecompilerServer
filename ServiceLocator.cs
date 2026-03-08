using DecompilerServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DecompilerServer;

/// <summary>
/// Simple service locator for MCP tools to access registered services.
/// Thread-safe to support concurrent test execution and MCP tool calls.
/// </summary>
public static class ServiceLocator
{
    private static readonly ThreadLocal<IServiceProvider?> _threadLocalProvider = new ThreadLocal<IServiceProvider?>();
    private static volatile IServiceProvider? _globalProvider;
    private static readonly object _lock = new object();

    /// <summary>
    /// Sets the service provider. Uses global storage for production, thread-local for tests.
    /// </summary>
    public static void SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        // Always set thread-local for test compatibility
        _threadLocalProvider.Value = serviceProvider;

        // Keep a global fallback for worker threads without thread-local state.
        lock (_lock)
        {
            _globalProvider = serviceProvider;
        }
    }

    /// <summary>
    /// Clears the thread-local service provider so worker threads fall back to the global provider.
    /// </summary>
    public static void ClearThreadLocalServiceProvider()
    {
        _threadLocalProvider.Value = null;
    }

    public static T GetRequiredService<T>() where T : notnull
    {
        // Try thread-local first (for tests)
        var provider = _threadLocalProvider.Value ?? _globalProvider;

        if (provider == null)
            throw new InvalidOperationException("Service provider not initialized for current thread");

        return provider.GetRequiredService<T>();
    }

    public static AssemblyContextManager ContextManager => GetRequiredService<AssemblyContextManager>();
    public static MemberResolver MemberResolver => GetRequiredService<MemberResolver>();
    public static DecompilerService DecompilerService => GetRequiredService<DecompilerService>();
    public static UsageAnalyzer UsageAnalyzer => GetRequiredService<UsageAnalyzer>();
    public static InheritanceAnalyzer InheritanceAnalyzer => GetRequiredService<InheritanceAnalyzer>();
    public static ResponseFormatter ResponseFormatter => GetRequiredService<ResponseFormatter>();
}
