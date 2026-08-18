using grader_app.Config;

Console.WriteLine("Hello, World!");

// Problems live outside the app, so the toml path comes in from the caller.
string tomlPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("GRADER_PROBLEM")
      ?? throw new ArgumentException("Pass a problem.toml path as the first argument.");

Problem problem = ProblemLoader.Load(tomlPath);

Console.WriteLine(problem.Meta.Title);        // "Two Sum"
Console.WriteLine(problem.Meta.Revision);     // 3
Console.WriteLine(problem.Tests.Count);       // 2

foreach (TestMeta test in problem.Tests)
{
    Console.WriteLine($"{test.Name}  sample={test.IsSample}");
    Console.WriteLine($"    in-path@  {test.InputPath}\n" +
                      $"    ans-path@ {test.AnsPath}");
    string input = File.ReadAllText(test.InputPath);
    string expected = File.ReadAllText(test.AnsPath);
}