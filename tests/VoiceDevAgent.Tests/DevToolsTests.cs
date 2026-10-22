using System.Xml.Linq;

public class SummarizeStatusTests
{
    static string[] Lines(string summary) => summary.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Reports_branch_counts_changes_renames_and_untracked_files()
    {
        string porcelain = string.Join('\n',
            "# branch.oid 6dfa58b4d775a4f4a5a887c75e3ad9c0825fc6b9",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/My File.cs",
            "2 R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 src/New.cs\tsrc/Old.cs",
            "? notes.txt",
            "");

        Assert.Equal(
            new[]
            {
                "branch main, 2 ahead, 1 behind",
                "2 changed, 1 untracked",
                "  .M src/My File.cs",
                "  R. src/New.cs (was src/Old.cs)",
                "  ?? notes.txt",
            },
            Lines(DevTools.SummarizeStatus(porcelain)));
    }

    [Fact]
    public void Handles_crlf_output_and_a_branch_without_upstream()
    {
        string porcelain = "# branch.oid (initial)\r\n# branch.head feature/x\r\n? a.txt\r\n";

        Assert.Equal(
            new[] { "branch feature/x, 0 ahead, 0 behind", "0 changed, 1 untracked", "  ?? a.txt" },
            Lines(DevTools.SummarizeStatus(porcelain)));
    }

    [Fact]
    public void Caps_the_file_lists()
    {
        string porcelain = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"? file{i}.txt"));

        string[] lines = Lines(DevTools.SummarizeStatus(porcelain));

        Assert.Equal("0 changed, 30 untracked", lines[1]);
        Assert.Equal(2 + 20, lines.Length);
    }
}

public class SummarizeBuildTests
{
    static readonly string Repo = Path.Combine(Path.GetTempPath(), "app");
    static readonly string Source = Path.Combine(Repo, "src", "A.cs");
    static readonly string Project = Path.Combine(Repo, "src", "App.csproj");

    [Fact]
    public void Deduplicates_diagnostics_lists_errors_first_and_makes_paths_relative()
    {
        string output = string.Join('\n',
            $"{Source}(3,1): warning CS0168: The variable 'x' is declared but never used [{Project}]",
            $"{Source}(10,5): error CS1061: 'Foo' does not contain a definition for 'Bar' [{Project}]",
            "",
            "Build FAILED.",
            $"{Source}(3,1): warning CS0168: The variable 'x' is declared but never used [{Project}]",
            $"{Source}(10,5): error CS1061: 'Foo' does not contain a definition for 'Bar' [{Project}]");

        string summary = DevTools.SummarizeBuild(output, 1, Repo);

        Assert.StartsWith("1 error(s), 1 warning(s).", summary);
        string error = "src/A.cs(10,5): error CS1061: 'Foo' does not contain a definition for 'Bar'" + Environment.NewLine;
        string warning = "src/A.cs(3,1): warning CS0168: The variable 'x' is declared but never used" + Environment.NewLine;
        Assert.Contains(error, summary);
        Assert.Contains(warning, summary);
        Assert.True(summary.IndexOf(error, StringComparison.Ordinal) < summary.IndexOf(warning, StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_zero_exit_with_no_located_errors_is_still_a_failure()
    {
        string output = "MSBUILD : error MSB1009: Project file does not exist.\nSwitch: Missing.csproj\n";

        string summary = DevTools.SummarizeBuild(output, 1, Repo);

        Assert.StartsWith("Build failed (exit code 1) with no file-located diagnostics.", summary);
        Assert.Contains("Switch: Missing.csproj", summary);
    }

    [Fact]
    public void A_clean_build_says_so() =>
        Assert.Equal("Build succeeded with no diagnostics.", DevTools.SummarizeBuild("Build succeeded.\n", 0, Repo));

    [Fact]
    public void Diagnostics_outside_the_repo_keep_their_full_path()
    {
        string targets = Path.Combine(Path.GetTempPath(), "sdk", "Microsoft.Common.targets");
        string output = $"{targets}(42,7): error MSB4018: The task failed unexpectedly. [{Project}]";

        string summary = DevTools.SummarizeBuild(output, 1, Repo);

        Assert.Contains($"{targets}(42,7): error MSB4018: The task failed unexpectedly.", summary);
    }

    [Fact]
    public void Caps_the_list_and_says_how_many_were_left_out()
    {
        string output = string.Join('\n',
            Enumerable.Range(1, 55).Select(i => $"{Source}({i},1): warning CS0168: unused [{Project}]"));

        string summary = DevTools.SummarizeBuild(output, 0, Repo);

        Assert.StartsWith("0 error(s), 55 warning(s).", summary);
        Assert.Contains("... 5 more suppressed.", summary);
        Assert.DoesNotContain("A.cs(51,1)", summary);
    }
}

public class SummarizeTrxTests
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    static XElement Result(string testName, string outcome, string? message = null, string? stackTrace = null) =>
        new(Ns + "UnitTestResult",
            new XAttribute("testName", testName),
            new XAttribute("outcome", outcome),
            new XElement(Ns + "Output",
                new XElement(Ns + "ErrorInfo",
                    message is null ? null : new XElement(Ns + "Message", message),
                    stackTrace is null ? null : new XElement(Ns + "StackTrace", stackTrace))));

    static XDocument Trx(XElement? counters, params XElement[] results) =>
        new(new XElement(Ns + "TestRun",
            new XElement(Ns + "Results", results),
            new XElement(Ns + "ResultSummary", counters)));

    [Fact]
    public void Reports_counts_and_failures_with_only_own_code_frames()
    {
        XDocument trx = Trx(
            new XElement(Ns + "Counters", new XAttribute("total", "2"), new XAttribute("passed", "1"), new XAttribute("failed", "1")),
            Result("Tests.CalcTests.Subtracts", "Passed"),
            Result("Tests.CalcTests.Adds", "Failed",
                "Assert.Equal() Failure\nExpected: 4\nActual:   5",
                "   at System.Runtime.Whatever()\n   at MyApp.Calc.Add(Int32 a) in Calc.cs:line 3\n"));

        string summary = DevTools.SummarizeTrx(trx);

        Assert.StartsWith("1/2 passed, 1 failed.", summary);
        Assert.Contains("FAILED Adds", summary);
        Assert.DoesNotContain("Subtracts", summary);
        Assert.Contains("  Expected: 4", summary);
        Assert.Contains("  at MyApp.Calc.Add(Int32 a) in Calc.cs:line 3", summary);
        Assert.DoesNotContain("System.Runtime", summary);
    }

    [Fact]
    public void Missing_counters_read_as_unknown() =>
        Assert.StartsWith("?/? passed, ? failed.", DevTools.SummarizeTrx(Trx(null)));
}

public sealed class ResolveProjectTests : IDisposable
{
    // <temp>/<id>/app is the repo; <temp>/<id>/app-evil is a sibling whose name starts with it.
    readonly string root = Path.Combine(Path.GetTempPath(), $"resolve-{Guid.NewGuid():N}");
    readonly string repo;

    public ResolveProjectTests()
    {
        repo = Path.Combine(root, "app");
        foreach (string file in new[] { "app/src/App/App.csproj", "app/App.slnx", "app/src/App/Program.cs", "app-evil/Evil.csproj" })
        {
            string path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("src/App/App.csproj")]
    [InlineData("src/App/../App/App.csproj")]
    [InlineData("App.slnx")]
    public void Accepts_a_project_or_solution_inside_the_repo(string project)
    {
        (string? path, string? error) = DevTools.ResolveProject(repo, project);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, project)), path);
    }

    [Fact]
    public void Accepts_an_absolute_path_inside_the_repo()
    {
        string absolute = Path.Combine(repo, "src", "App", "App.csproj");

        Assert.Equal((absolute, null), DevTools.ResolveProject(repo, absolute));
    }

    [Theory]
    [InlineData("-bl:build.binlog")]
    [InlineData("-p:Configuration=Release")]
    [InlineData("--source")]
    public void Refuses_options(string project) =>
        Assert.Contains("is not a project path", DevTools.ResolveProject(repo, project).Error);

    [Theory]
    [InlineData("../app-evil/Evil.csproj")]
    [InlineData("src/../../app-evil/Evil.csproj")]
    public void Refuses_paths_that_escape_the_repo_even_when_the_file_exists(string project) =>
        Assert.Contains("outside the repository", DevTools.ResolveProject(repo, project).Error);

    [Fact]
    public void Refuses_an_absolute_path_outside_the_repo() =>
        Assert.Contains("outside the repository",
            DevTools.ResolveProject(repo, Path.Combine(root, "app-evil", "Evil.csproj")).Error);

    [Theory]
    [InlineData("src/App/Program.cs")]
    [InlineData("Directory.Build.targets")]
    [InlineData("src/App")]
    public void Refuses_anything_that_is_not_a_project_or_solution(string project) =>
        Assert.Contains("is not a project or solution", DevTools.ResolveProject(repo, project).Error);

    [Fact]
    public void Refuses_a_missing_project() =>
        Assert.Contains("does not exist", DevTools.ResolveProject(repo, "src/Missing/Missing.csproj").Error);

    [Fact]
    public void Refuses_an_invalid_path() =>
        Assert.Contains("not a valid path", DevTools.ResolveProject(repo, "src/A\0.csproj").Error);

    [Fact]
    public void The_refused_value_never_comes_back_as_a_path() =>
        Assert.Null(DevTools.ResolveProject(repo, "-bl:build.binlog").Path);
}
