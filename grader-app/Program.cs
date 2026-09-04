using grader_app.Config;
using Tomlyn;

string text = File.ReadAllText("sample.toml");
var problemData = TomlSerializer.Deserialize<ProblemFile>(text);

Console.WriteLine(problemData.Problem.Title);
foreach (var tc in problemData.Problem.Samples) {
    Console.WriteLine(tc);
}