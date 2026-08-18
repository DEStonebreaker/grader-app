using System.Globalization;
using grader_app.Config;

namespace grader_app.Isolate;

// Argument lists for the `isolate` binary. CliWrap wants the target and its
// arguments separately, so these return the arguments only.
public static class Commands
{
    public static string[] Init(uint boxId) =>
        [$"--box-id={boxId}", "--init"];

    public static string[] Cleanup(uint boxId) =>
        [$"--box-id={boxId}", "--cleanup"];

    // Paths passed to --stdin/--stdout/--stderr are resolved *inside* the box,
    // so they are bare file names. --meta is written on the host.
    public static string[] Run(
        uint boxId,
        Problem problem,
        TestMeta test,
        string metaPath,
        string executable)
    {
        double timeSec = problem.Limits.TimeSec ?? 4.0;
        int memoryKb = problem.Limits.CgMemoryKb ?? 262144;

        // A comma decimal separator would make isolate reject the limit.
        string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

        return
        [
            $"--box-id={boxId}",
            $"--meta={metaPath}",
            $"--time={Number(timeSec)}",
            $"--wall-time={Number(timeSec * 2 + 1)}",
            $"--cg-mem={memoryKb}",
            $"--stdin={test.Name}.in",
            $"--stdout={test.Name}.out",
            $"--stderr={test.Name}.err",
            "--run",
            "--",
            $"./{executable}",
        ];
    }
}