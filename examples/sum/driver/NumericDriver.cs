using System.Globalization;
using grader_app.Grading;

namespace SumDriver;

// Trusted host code: only context.RunAsync executes the student program.
public sealed class NumericDriver : IGradingDriver
{
    public async Task<CheckResult> GradeAsync(GradingContext context, CancellationToken cancellationToken)
    {
        var result = await context.RunAsync(cancellationToken);
        var expected = long.Parse(context.ExpectedOutput.Trim(), CultureInfo.InvariantCulture);
        var passed = long.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var actual) && actual == expected;
        return new CheckResult(passed, passed ? null : "Expected a single integer equal to the sum.");
    }
}
