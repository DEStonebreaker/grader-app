using Xunit;

namespace grader_app.Tests;

public sealed class RealIsolateFactAttribute : FactAttribute
{
    public RealIsolateFactAttribute()
    {
        // Real sandbox tests are opt-in because they require Linux, Isolate, and judge-user privileges.
        if (!OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("VALIMARK_RUN_ISOLATE_TESTS") != "1")
        {
            Skip = "Set VALIMARK_RUN_ISOLATE_TESTS=1 on a configured Linux judge host.";
        }
    }
}
