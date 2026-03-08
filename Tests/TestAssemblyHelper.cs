namespace Tests;

internal static class TestAssemblyHelper
{
    internal static string GetTestAssemblyPath()
    {
        var outputAssembly = Path.Combine(AppContext.BaseDirectory, "test.dll");
        if (File.Exists(outputAssembly))
        {
            return Path.GetFullPath(outputAssembly);
        }

        var repoAssembly = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "TestLibrary", "bin", "Debug", "net8.0", "test.dll"));

        if (File.Exists(repoAssembly))
        {
            return repoAssembly;
        }

        throw new FileNotFoundException(
            $"Test library not found. Searched '{outputAssembly}' and '{repoAssembly}'.");
    }

    internal static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }
}
