using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// The three tools from Part 1 of the series, plus the helper that runs their processes.
// Part 1 shows them as top-level functions; here they live in a class so the agent can call them.
static partial class DevTools
{
    public static async Task<string> GitStatusAsync(string repo, CancellationToken ct)
    {
        ProcessResult git = await RunAsync("git", ["status", "--porcelain=v2", "--branch"], repo, ct);
        if (git.ExitCode != 0) return $"git status failed (exit code {git.ExitCode}): {git.Output.Trim()}";
        return SummarizeStatus(git.Output);
    }

    internal static string SummarizeStatus(string output)
    {
        string branch = "(detached)";
        int ahead = 0, behind = 0;
        List<string> changed = [], untracked = [];

        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("# branch.head "))
            {
                branch = line["# branch.head ".Length..];
            }
            else if (line.StartsWith("# branch.ab "))
            {
                string[] ab = line["# branch.ab ".Length..].Split(' ');
                ahead = int.Parse(ab[0]);    // "+2"
                behind = -int.Parse(ab[1]);  // "-0": git writes the count with a minus sign
            }
            else if (line.StartsWith("1 "))
            {
                // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                string[] f = line.Split(' ', 9);
                changed.Add($"{f[1]} {f[8]}");
            }
            else if (line.StartsWith("2 "))
            {
                // renames carry an extra score field, and the path field is "new<tab>old"
                string[] f = line.Split(' ', 10);
                string[] paths = f[9].Split('\t');
                changed.Add($"{f[1]} {paths[0]} (was {paths[1]})");
            }
            else if (line.StartsWith("? "))
            {
                untracked.Add(line[2..]);
            }
        }

        StringBuilder summary = new();
        summary.AppendLine($"branch {branch}, {ahead} ahead, {behind} behind");
        summary.AppendLine($"{changed.Count} changed, {untracked.Count} untracked");
        foreach (string entry in changed.Take(40)) summary.AppendLine($"  {entry}");
        foreach (string entry in untracked.Take(20)) summary.AppendLine($"  ?? {entry}");
        return summary.ToString();
    }

    // A source-generated regex, so the pattern is compiled at build time rather than
    // at startup. It lives in a class because a file-based app has no room for a
    // field: anything declared alongside top-level statements has to be a local, and
    // `static readonly` there is a CS0106.

    public static async Task<string> DotnetBuildAsync(string repo, string? project, CancellationToken ct)
    {
        string[] args = project is null
            ? ["build", "--nologo"]
            : ["build", "--nologo", project];

        ProcessResult build = await RunAsync("dotnet", args, repo, ct);
        return SummarizeBuild(build.Output, build.ExitCode, repo);
    }

    internal static string SummarizeBuild(string output, int exitCode, string repo)
    {
        // MSBuild emits each diagnostic inline and then again in the summary block.
        Dictionary<(string, int, int, string), Diagnostic> unique = [];

        foreach (string raw in output.Split('\n'))
        {
            Match match = BuildOutput.DiagnosticLine().Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;

            string file = match.Groups["file"].Value.Trim();

            // Diagnostics anchored in the SDK's own .targets files are infrastructure
            // failures, not code failures. Keep them, but don't pretend they're yours.
            string display = file.StartsWith(repo, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(repo, file).Replace('\\', '/')
                : file;

            Diagnostic diagnostic = new(
                display,
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["col"].Value),
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim());

            unique.TryAdd((diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Code), diagnostic);
        }

        List<Diagnostic> errors = [.. unique.Values.Where(d => d.Severity == "error")];
        List<Diagnostic> warnings = [.. unique.Values.Where(d => d.Severity == "warning")];

        // The exit code is the verdict; the parsed lines are only the explanation. A failure
        // with no file and line (a missing project, a bad argument) still has to read as one.
        if (exitCode != 0 && errors.Count == 0)
            return $"Build failed (exit code {exitCode}) with no file-located diagnostics. Last lines:\n" +
                   string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(10));

        if (errors.Count == 0 && warnings.Count == 0) return "Build succeeded with no diagnostics.";

        StringBuilder summary = new();
        summary.AppendLine($"{errors.Count} error(s), {warnings.Count} warning(s).");

        // Errors first: a warning is rarely why the build is red.
        foreach (Diagnostic d in errors.Concat(warnings).Take(50))
            summary.AppendLine($"{d.File}({d.Line},{d.Column}): {d.Severity} {d.Code}: {d.Message}");

        int total = errors.Count + warnings.Count;
        if (total > 50) summary.AppendLine($"... {total - 50} more suppressed.");

        return summary.ToString();
    }

    public static async Task<string> DotnetTestAsync(string repo, CancellationToken ct)
    {
        string resultsDir = Path.Combine(Path.GetTempPath(), $"agent-trx-{Guid.NewGuid():N}");
        try
        {
            ProcessResult run = await RunAsync("dotnet",
                ["test", "--nologo", "--logger", "trx;LogFileName=run.trx", "--results-directory", resultsDir],
                repo, ct);

            string trx = Path.Combine(resultsDir, "run.trx");
            if (!File.Exists(trx)) return $"No TRX produced (exit code {run.ExitCode}) — the test project probably failed to build.";

            return SummarizeTrx(XDocument.Load(trx));
        }
        finally
        {
            if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
        }
    }

    internal static string SummarizeTrx(XDocument doc)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        XElement? counters = doc.Descendants(ns + "Counters").FirstOrDefault();
        StringBuilder summary = new();
        summary.AppendLine(
            $"{counters?.Attribute("passed")?.Value ?? "?"}/{counters?.Attribute("total")?.Value ?? "?"} passed, " +
            $"{counters?.Attribute("failed")?.Value ?? "?"} failed.");

        foreach (XElement result in doc.Descendants(ns + "UnitTestResult")
                                       .Where(r => (string?)r.Attribute("outcome") == "Failed")
                                       .Take(15))
        {
            string name = ((string?)result.Attribute("testName") ?? "(unknown)").Split('.')[^1];
            string message = result.Descendants(ns + "Message").FirstOrDefault()?.Value.Trim() ?? "";

            // Keep only frames in the project's own code.
            IEnumerable<string> frames = (result.Descendants(ns + "StackTrace").FirstOrDefault()?.Value ?? "")
                .Split('\n')
                .Select(f => f.Trim())
                .Where(f => f.Length > 0 && !f.StartsWith("at System."))
                .Take(3);

            summary.AppendLine();
            summary.AppendLine($"FAILED {name}");
            foreach (string line in message.Split('\n')) summary.AppendLine($"  {line.TrimEnd('\r')}");
            foreach (string frame in frames) summary.AppendLine($"  {frame}");
        }

        return summary.ToString();
    }

    public static async Task<ProcessResult> RunAsync(string file, string[] args, string cwd, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = file,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // ArgumentList escapes each value individually. Never concatenate a command
        // string from model-supplied input.
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        // `dotnet run` on a project exports MSBuildSDKsPath and MSBuildExtensionsPath for the
        // SDK that built the AGENT. A child `dotnet build` would inherit them and load that
        // SDK's targets even when the repo's global.json picks another one.
        foreach (string key in psi.Environment.Keys
                     .Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {file}.");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} did not finish within five minutes.");
        }

        return new(await stdout + await stderr, process.ExitCode);
    }

}

static partial class BuildOutput
{
    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\): (?<severity>error|warning) (?<code>[A-Za-z]+\d+): (?<message>.+?)(?: \[[^\]]+\])?$")]
    public static partial Regex DiagnosticLine();
}

record Diagnostic(string File, int Line, int Column, string Severity, string Code, string Message);
record ProcessResult(string Output, int ExitCode);
