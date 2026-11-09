using grader_app.Config;
using Tomlyn;

namespace grader_app.Grading;

public sealed record TestCase(string Name, string InputPath, string ExpectedPath);

public sealed record LoadedProblem(
    string Title, LimitSection Limits, IReadOnlyList<TestCase> Cases,
    string Checker, string? DriverAssembly, string? DriverType);

public static class ProblemLoader
{
    public static LoadedProblem Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifest = ResolveFile(root, "problem.toml");
        var problem = TomlSerializer.Deserialize<ProblemFile>(File.ReadAllText(manifest))
                      ?? throw new InvalidDataException("Problem configuration is empty.");
        ValidateLimits(problem.Limits);
        if (string.IsNullOrWhiteSpace(problem.Problem.Title))
            throw new InvalidDataException("problem.title is required.");
        if (problem.Tests.Count == 0)
            throw new InvalidDataException("At least one [[tests]] entry is required.");
        if (problem.Tests.Any(test => string.IsNullOrWhiteSpace(test.Name)) ||
            problem.Tests.Select(test => test.Name).Distinct(StringComparer.Ordinal).Count() != problem.Tests.Count)
            throw new InvalidDataException("Test names must be nonempty and unique.");
        if (problem.Grading.Checker is not ("tokens" or "exact"))
            throw new InvalidDataException("grading.checker must be 'tokens' or 'exact'.");
        var hasAssembly = !string.IsNullOrWhiteSpace(problem.Grading.DriverAssembly);
        var hasType = !string.IsNullOrWhiteSpace(problem.Grading.DriverType);
        if (hasAssembly != hasType)
            throw new InvalidDataException("Set both grading.driver_assembly and grading.driver_type.");
        var cases = problem.Tests.Select(test => new TestCase(test.Name,
            ResolveFile(root, test.Input), ResolveFile(root, test.Expected))).ToArray();
        // Manifest order is the grading order; paths are rooted in the problem, not the caller's cwd.
        return new LoadedProblem(problem.Problem.Title, problem.Limits, cases, problem.Grading.Checker,
            hasAssembly ? ResolveFile(root, problem.Grading.DriverAssembly!) : null,
            hasType ? problem.Grading.DriverType : null);
    }

    public static void ValidateLimits(LimitSection limits)
    {
        if (!double.IsFinite(limits.CtSeconds) || limits.CtSeconds <= 0 ||
            !double.IsFinite(limits.RtSeconds) || limits.RtSeconds <= 0 ||
            !double.IsFinite(limits.CtSeconds * 2) || !double.IsFinite(limits.RtSeconds * 2) ||
            limits.CtMemoryKb == 0 || limits.RtMemoryKb == 0)
            throw new InvalidDataException("Compilation/runtime time and memory limits must be positive and finite.");
    }

    internal static string ResolveFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Problem file paths must be relative and nonempty.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Problem file escapes its folder: {relative}");
        // Reject linked assets and linked directories below the chosen problem root.
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
                throw new InvalidDataException($"Problem assets cannot be symbolic links: {relative}");
        }
        if (!File.Exists(path))
            throw new FileNotFoundException($"Problem file does not exist: {relative}", path);
        return path;
    }
}
