using System.Globalization;
using grader_app.Config;

namespace grader_app.Isolate;

// Argument lists for the `isolate` binary. CliWrap wants the target and its
// arguments separately, so these return the arguments only.
public static class Commands
{
    // Output size cap, so one runaway submission cannot fill the box tmpfs.
    private const int MaxOutputKb = 8192;
    private const int StackKb = 65536;

    public static string[] Init(uint boxId) =>
        ["--cg", $"--box-id={boxId}", "--init"];

    public static string[] Cleanup(uint boxId) =>
        ["--cg", $"--box-id={boxId}", "--cleanup"];

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

        // No --share-net: the default empty network namespace is what we want.
        return
        [
            "--cg",
            $"--box-id={boxId}",
            $"--meta={metaPath}",
            "--processes=1",
            $"--time={Number(timeSec)}",
            $"--wall-time={Number(timeSec * 2 + 1)}",
            "--extra-time=0.5",
            $"--cg-mem={memoryKb}",
            $"--fsize={MaxOutputKb}",
            $"--stack={StackKb}",
            "--env=PATH=/usr/bin:/bin",
            "--env=HOME=/box",
            $"--stdin={test.Name}.in",
            $"--stdout={test.Name}.out",
            $"--stderr={test.Name}.err",
            "--run",
            "--",
            $"./{executable}",
        ];
    }
}