using grader_app.Config;
using grader_app.Isolate;

// Problems live outside the app, so the toml path comes in from the caller.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: grader-app <problem.toml> <executable> [box-id]");
    return 2;
}

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("grader-app runs on the Linux judge host: isolate is not available here.");
    return 2;
}

string tomlPath = args[0];
string executablePath = Path.GetFullPath(args[1]);
uint boxId = args.Length > 2 ? uint.Parse(args[2]) : 0;

Problem problem = ProblemLoader.Load(tomlPath);
List<TestResult> results = await new ProblemRunner(problem, boxId).Run(executablePath);

Console.WriteLine($"{problem.Meta.Title} (rev {problem.Meta.Revision})  box={boxId}");
Console.WriteLine(new string('-', 64));

foreach (TestResult result in results)
{
    string mark = result.Verdict == Verdict.Accepted ? "PASS" : "FAIL";
    string label = result.Test.IsSample ? "sample" : "hidden";

    Console.WriteLine(
        $"{mark} {result.Test.Name,-6} {label,-7} " +
        $"{result.Verdict,-18} {result.TimeSec,6:0.000}s {result.MemoryKb,8} KB");

    if (result.Verdict == Verdict.Accepted)
    {
        continue;
    }

    if (result.Message.Length > 0)
    {
        Console.WriteLine($"    {result.Message}");
    }

    // Hidden tests report the verdict only, so their data stays unpublished.
    if (result.Test.IsSample && result.Diff.Length > 0)
    {
        Console.WriteLine($"    expected: {Inline(result.Expected)}");
        Console.WriteLine($"    actual:   {Inline(result.Actual)}");
        Console.WriteLine(Indent(Truncate(result.Diff)));
    }
}

int passed = results.Count(r => r.Verdict == Verdict.Accepted);
Console.WriteLine(new string('-', 64));
Console.WriteLine($"{passed}/{results.Count} passed");

return passed == results.Count ? 0 : 1;

// Whitespace is usually what differs, so make it visible on a single line.
static string Inline(string text)
{
    string flat = text.TrimEnd().Replace("\n", "\\n").Replace("\t", "\\t");
    return flat.Length > 120 ? flat[..120] + "..." : flat;
}

static string Truncate(string diff)
{
    const int maxLines = 20;
    string[] lines = diff.TrimEnd().Split('\n');

    return lines.Length <= maxLines
        ? string.Join('\n', lines)
        : string.Join('\n', lines.Take(maxLines)) + $"\n... {lines.Length - maxLines} more lines";
}

static string Indent(string text) =>
    string.Join('\n', text.Split('\n').Select(line => "    | " + line));