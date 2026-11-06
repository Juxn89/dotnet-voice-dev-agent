using System.Text;
using System.Text.RegularExpressions;

// Part 3's one new development tool. Part 2's live session ended on "I can't read the code
// from here"; this is the missing tool. It only reads: unlike dotnet_build and dotnet_test,
// which run the repository's own code, it executes nothing.
static partial class DevTools
{
    const int MaxReadLines = 200;
    const int MaxReadChars = 16_000;
    const long MaxReadBytes = 1_000_000;

    // Whatever this tool returns is sent to the model's API. A build agent never needs these.
    // It is a denylist, so it is incomplete: anything else in the repository can reach the API.
    static readonly string[] SecretExtensions = [".pfx", ".p12", ".key", ".pem", ".snk", ".env"];
    static readonly string[] SecretNames = ["id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "secrets.json"];

    /// <summary>
    /// Up to 200 numbered lines, and 16,000 characters, of one file inside the repository. The
    /// numbers let the model say "line 111", and ask for the next range itself when a file is longer.
    /// </summary>
    internal static (string Content, bool IsError) ReadFile(string repo, string path, int? startLine, int? endLine)
    {
        string full, root = RepoRoot(repo);
        try
        {
            full = Path.GetFullPath(Path.Combine(repo, path));
        }
        catch (ArgumentException)
        {
            return ($"\"{path}\" is not a valid path.", true);
        }

        if (!full.StartsWith(root, PathComparison)) return ($"\"{path}\" is outside the repository.", true);

        string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        // On NTFS "id_rsa::$DATA" opens id_rsa and ".git::$INDEX_ALLOCATION" opens .git, while
        // every check below compares names. No file a build agent needs has a colon in its path.
        if (relative.Contains(':')) return ($"\"{relative}\" has a colon in it; ask with the plain file name.", true);
        string[] parts = relative.Split('/');
        string name = parts[^1];
        // "GIT~1" is the 8.3 short name Windows can give ".git"; the name checks below would miss it.
        if (parts.Any(part => ShortName().IsMatch(part)))
            return ($"\"{relative}\" uses a short (8.3) name; ask with the full name.", true);
        if (parts.Contains(".git", StringComparer.OrdinalIgnoreCase))
            return ($"\"{relative}\" is inside .git.", true);
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || SecretExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            || SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return ($"\"{relative}\" may hold secrets and is not read.", true);

        // A link anywhere on the way, a folder symlink or a junction as much as the file
        // itself, can point outside the repository; the checks above only saw names.
        string current = root;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null) return ($"\"{relative}\" goes through a link, which is not followed.", true);
        }

        FileInfo file = new(full);
        if (!file.Exists) return ($"\"{relative}\" does not exist.", true);
        if (file.Length > MaxReadBytes) return ($"\"{relative}\" is {file.Length:N0} bytes, too large to read.", true);

        string text = File.ReadAllText(full);
        if (text.Contains('\0')) return ($"\"{relative}\" is a binary file.", true);

        string[] lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int from = Math.Max(1, startLine ?? 1);
        if (from > lines.Length) return ($"\"{relative}\" has {lines.Length} lines.", true);
        int to = Math.Clamp(endLine ?? int.MaxValue, from, Math.Min(lines.Length, from + MaxReadLines - 1));

        // A minified file can hold all of its size on one line, and whatever this returns stays in
        // a history that can only grow at the end. So a read also stops at a character budget.
        StringBuilder body = new();
        int last = from - 1;
        for (int i = from; i <= to; i++)
        {
            string line = lines[i - 1];
            if (body.Length + line.Length > MaxReadChars)
            {
                if (i == from)
                {
                    body.Append($"{i,5}  {line[..MaxReadChars]} [line cut at {MaxReadChars} characters]\n");
                    last = i;
                }
                break;
            }
            body.Append($"{i,5}  {line}\n");
            last = i;
        }
        return ($"{relative}, lines {from}-{last} of {lines.Length}\n{body}", false);
    }

    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortName();
}
