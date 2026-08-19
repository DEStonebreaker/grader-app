using System.Globalization;
using System.Runtime.Versioning;
using CliWrap;
using CliWrap.Buffered;
using grader_app.Config;

namespace grader_app.Isolate;

public enum Verdict
{
    Accepted,
    WrongAnswer,
    TimeLimitExceeded,
    RuntimeError,
    InternalError,
}

public sealed record TestResult
{
    public required TestMeta Test { get; init; }
    public required Verdict Verdict { get; init; }
    public double TimeSec { get; init; }
    public int MemoryKb { get; init; }

    // Empty unless the run produced them.
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";
    public string Diff { get; init; } = "";
    public string Message { get; init; } = "";
}

// Isolate is Linux-only, so this whole type is.
[SupportedOSPlatform("linux")]
public sealed class ProblemRunner
{
    public ProblemRunner(Problem problem, uint boxId)
    {
        _problem = problem;
        BoxId = boxId;
    }

    public uint BoxId { get; }

    /// <summary>
    /// Initialises a box, runs every test against <paramref name="executablePath"/>,
    /// then tears the box down.
    /// </summary>
    public async Task<List<TestResult>> Run(string executablePath)
    {
        // A leftover box from a crashed run would make --init fail.
        await Isolate(Commands.Cleanup(BoxId));

        BufferedCommandResult init = await Isolate(Commands.Init(BoxId));
        if (init.ExitCode != 0)
        {
            throw new InvalidOperationException($"isolate --init failed: {init.StandardError.Trim()}");
        }

        // --init prints the box root; the working directory is its `box` subdirectory.
        string boxDir = Path.Combine(init.StandardOutput.Trim(), "box");
        string metaDir = Directory.CreateTempSubdirectory("grader-meta-").FullName;

        try
        {
            File.Copy(executablePath, Path.Combine(boxDir, Executable), overwrite: true);
            File.SetUnixFileMode(
                Path.Combine(boxDir, Executable),
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            List<TestResult> results = new();
            foreach (TestMeta test in _problem.Tests)
            {
                results.Add(await RunTest(test, boxDir, metaDir));
            }

            return results;
        }
        finally
        {
            Directory.Delete(metaDir, recursive: true);
            await Isolate(Commands.Cleanup(BoxId));
        }
    }

    private async Task<TestResult> RunTest(TestMeta test, string boxDir, string metaDir)
    {
        // Isolate can only read files that live inside the box.
        File.Copy(test.InputPath, Path.Combine(boxDir, $"{test.Name}.in"), overwrite: true);

        string metaPath = Path.Combine(metaDir, $"{test.Name}.meta");
        string outPath = Path.Combine(boxDir, $"{test.Name}.out");
        string errPath = Path.Combine(boxDir, $"{test.Name}.err");

        await Isolate(Commands.Run(BoxId, _problem, test, metaPath, Executable));

        Dictionary<string, string> meta = ReadMeta(metaPath);
        double timeSec = ParseDouble(meta, "time");
        int memoryKb = (int)ParseDouble(meta, "cg-mem");
        meta.TryGetValue("status", out string? status);
        meta.TryGetValue("message", out string? message);

        // RE / SG / TO / XX mean the program never produced a comparable answer.
        if (status is not null)
        {
            return new TestResult
            {
                Test = test,
                Verdict = status switch
                {
                    "TO" => Verdict.TimeLimitExceeded,
                    "RE" or "SG" => Verdict.RuntimeError,
                    _ => Verdict.InternalError,
                },
                TimeSec = timeSec,
                MemoryKb = memoryKb,
                Message = string.Join(
                    " | ",
                    new[] { message, ReadOrEmpty(errPath).Trim() }.Where(s => !string.IsNullOrEmpty(s))),
            };
        }

        BufferedCommandResult diff = await Cli.Wrap("diff")
            .WithArguments(["-u", test.AnsPath, outPath])
            .WithWorkingDirectory("/")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        return new TestResult
        {
            Test = test,
            Verdict = diff.ExitCode == 0 ? Verdict.Accepted : Verdict.WrongAnswer,
            TimeSec = timeSec,
            MemoryKb = memoryKb,
            Expected = ReadOrEmpty(test.AnsPath),
            Actual = ReadOrEmpty(outPath),
            Diff = diff.ExitCode == 0 ? "" : diff.StandardOutput,
        };
    }

    // Isolate exits non-zero for a failed run, which is a normal outcome here.
    //
    // The working directory is pinned to "/" because .NET chdir()s into it in the
    // forked child: inheriting a caller's home directory fails with EACCES when the
    // grader runs as the judge account.
    private static Task<BufferedCommandResult> Isolate(string[] arguments) =>
        Cli.Wrap("isolate")
            .WithArguments(arguments)
            .WithWorkingDirectory("/")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

    // The meta file is one `key:value` per line.
    private static Dictionary<string, string> ReadMeta(string path)
    {
        Dictionary<string, string> meta = new();
        if (!File.Exists(path))
        {
            return meta;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            int separator = line.IndexOf(':');
            if (separator > 0)
            {
                meta[line[..separator]] = line[(separator + 1)..];
            }
        }

        return meta;
    }

    private static double ParseDouble(Dictionary<string, string> meta, string key) =>
        meta.TryGetValue(key, out string? value)
        && double.TryParse(value, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;

    private static string ReadOrEmpty(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : "";

    private const string Executable = "executable.o";
    private readonly Problem _problem;
}