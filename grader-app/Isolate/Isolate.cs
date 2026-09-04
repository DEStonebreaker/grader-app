using CliWrap;
using grader_app.Config;

namespace grader_app.Isolate;

public static class Isolate
{
    /* Isolate Commands */
    public static List<string> InitBoxCmd(uint boxId)
    {
        return ["isolate", $"--box-id={boxId}", "--init"];
    }
    
    public static List<string> CleanupBoxCmd(uint boxId)
    {
        return ["isolate", $"--box-id={boxId}", "--cleanup"];
    }

    public static List<string> RunCmdBase(uint boxId, LimitSection limits)
    {
        var cmd = new List<string>(["isolate", "--cg", $"--box-id={boxId}"]);
        
        cmd.Add($"--time={limits.CtSeconds}");
        cmd.Add($"--wall-time={limits.CtSeconds * 2.0}");
        cmd.Add($"--cg-mem={limits.CtMemoryKb}");
        cmd.Add($"--run");
        cmd.Add($"--");
        
        return cmd;
    }
}