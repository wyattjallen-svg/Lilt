using System.Runtime.CompilerServices;

namespace Lilt.Core.Tests;

// Minimal runner so the core tests need no NuGet packages: `dotnet run --project tests/Lilt.Core.Tests`.
static class Check
{
    static int passed, failed;
    static string current = "";

    public static void Test(string name, Action body)
    {
        current = name;
        try
        {
            body();
            passed++;
        }
        catch (Exception e)
        {
            failed++;
            Console.WriteLine($"FAIL  {name}\n      {e.Message}");
        }
    }

    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new Exception($"expected true: {expr}");
    }

    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{expr}: expected <{expected}> but was <{actual}>");
    }

    public static int Summary()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    public static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lilt.slnx"))) dir = dir.Parent;
        if (dir is null) throw new Exception($"repo root not found (needed for {relative}, test '{current}')");
        return Path.Combine(dir.FullName, relative);
    }
}
