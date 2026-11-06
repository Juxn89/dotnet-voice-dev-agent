using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The storage behind Claude's memory tool (memory_20250818). The model asks for file
/// operations on a virtual /memories directory; this class runs them on a real folder, one per
/// repository, outside the repository so a memory is never committed. Every path and every
/// line of text the model sends is untrusted input.
/// </summary>
sealed partial class MemoryStore
{
    public const string Root = "/memories";

    // Memory is loaded into the first request of every session, so its size is paid every
    // morning. These caps keep it to roughly 1,500 tokens; past them the model is told to
    // merge or delete entries instead of appending more.
    const int MaxFileChars = 4_000;
    const int MaxTotalChars = 6_000;

    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    readonly string directory;   // full path, ending in a separator

    public MemoryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        this.directory = DevTools.WithSeparator(Path.GetFullPath(directory));

        // A crash between writing a temp file and moving it leaves the temp file behind.
        // It is hidden, so nothing else would ever count or remove it.
        foreach (string orphan in Directory.EnumerateFiles(this.directory, ".tmp-*"))
        {
            try
            {
                File.Delete(orphan);
            }
            catch (IOException)
            {
                // Not an orphan: another session's write is still in flight, and it moves its own file.
            }
        }
    }

    /// <summary>One folder per repository, under the user's local application data.</summary>
    public static MemoryStore ForRepository(string repo)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo));
        string key = PathComparison == StringComparison.OrdinalIgnoreCase ? full.ToLowerInvariant() : full;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        string name = Path.GetFileName(full) is { Length: > 0 } folder ? folder : "repo";
        return new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceDevAgent", "memories", $"{name}-{hash}"));
    }

    /// <summary>Runs one memory command. Failures go back to the model as text, never as exceptions.</summary>
    public (string Content, bool IsError) Run(IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            using FileStream gate = Lock();
            string result = Text(input, "command") switch
            {
                "view" => View(Text(input, "path"), input.GetValueOrDefault("view_range")),
                "create" => Create(Text(input, "path"), Text(input, "file_text")),
                "str_replace" => Replace(Text(input, "path"), Text(input, "old_str"), OptionalText(input, "new_str")),
                "insert" => Insert(Text(input, "path"), Number(input, "insert_line"), Text(input, "insert_text")),
                "delete" => Delete(Text(input, "path")),
                "rename" => Rename(Text(input, "old_path"), Text(input, "new_path")),
                string other => throw new MemoryException($"Error: unknown command {other}"),
            };
            return (result, false);
        }
        catch (MemoryException ex)
        {
            return (ex.Message, true);
        }
    }

    /// <summary>Every memory file, for the system prompt at the start of a session. Empty when there are none.</summary>
    public string Snapshot()
    {
        StringBuilder all = new();
        foreach (string file in Files().Order(StringComparer.Ordinal))
            all.Append($"<memory path=\"{ToVirtual(file)}\">\n{File.ReadAllText(file).TrimEnd()}\n</memory>\n");
        return all.ToString();
    }

    string View(string path, JsonElement range)
    {
        string full = Resolve(path);
        if (Directory.Exists(full))
        {
            StringBuilder listing = new($"Here're the files and directories up to 2 levels deep in {path}, excluding hidden items and node_modules:\n");
            listing.Append($"{Size(Visible(full).Where(File.Exists).Sum(f => new FileInfo(f).Length))}\t{path}\n");
            foreach (string entry in Visible(full).Order(StringComparer.Ordinal))
                listing.Append($"{Size(File.Exists(entry) ? new FileInfo(entry).Length : 0)}\t{ToVirtual(entry)}\n");
            return listing.ToString().TrimEnd();
        }
        if (!File.Exists(full)) throw new MemoryException($"The path {path} does not exist. Please provide a valid path.");

        string[] lines = Lines(File.ReadAllText(full));
        int from = 1, to = lines.Length;
        if (range.ValueKind != JsonValueKind.Undefined)
        {
            // [1.5, 10] or [1, 1e10] would make GetInt32 throw; anything but two integers is an answer, not a crash.
            if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2
                || !range[0].TryGetInt32(out int start) || !range[1].TryGetInt32(out int end))
                throw new MemoryException("Error: `view_range` must be two integers, [start, end] or [start, -1]");
            from = Math.Max(1, start);
            to = end == -1 ? lines.Length : Math.Min(end, lines.Length);
        }
        StringBuilder numbered = new($"Here's the content of {path} with line numbers:\n");
        for (int i = from; i <= to; i++) numbered.Append($"{i,6}\t{lines[i - 1]}\n");
        return numbered.ToString().TrimEnd('\n');
    }

    string Create(string path, string text)
    {
        // The tool tells the model that create "creates or overwrites", so overwrite.
        string full = Resolve(path);
        if (Directory.Exists(full)) throw new MemoryException($"Error: {path} is a directory");
        Write(path, full, text);
        return $"File created successfully at: {path}";
    }

    string Replace(string path, string oldText, string newText)
    {
        string full = ExistingFile(path, $"Error: The path {path} does not exist. Please provide a valid path.");
        string text = File.ReadAllText(full);
        List<int> at = [];
        for (int i = text.IndexOf(oldText, StringComparison.Ordinal); i >= 0 && oldText.Length > 0;
             i = text.IndexOf(oldText, i + 1, StringComparison.Ordinal))
            at.Add(i);

        if (at.Count == 0)
            throw new MemoryException($"No replacement was performed, old_str `{oldText}` did not appear verbatim in {path}.");
        if (at.Count > 1)
        {
            string lines = string.Join(", ", at.Select(i => text.AsSpan(0, i).Count('\n') + 1));
            throw new MemoryException($"No replacement was performed. Multiple occurrences of old_str `{oldText}` in lines: {lines}. Please ensure it is unique");
        }
        Write(path, full, string.Concat(text.AsSpan(0, at[0]), newText, text.AsSpan(at[0] + oldText.Length)));
        return "The memory file has been edited.";
    }

    string Insert(string path, int line, string insertText)
    {
        string full = ExistingFile(path, $"Error: The path {path} does not exist");
        List<string> lines = [.. Lines(File.ReadAllText(full))];
        if (line < 0 || line > lines.Count)
            throw new MemoryException($"Error: Invalid `insert_line` parameter: {line}. It should be within the range of lines of the file: [0, {lines.Count}]");
        lines.Insert(line, insertText.TrimEnd('\n'));
        Write(path, full, string.Join('\n', lines) + "\n");
        return $"The file {path} has been edited.";
    }

    string Delete(string path)
    {
        string full = Resolve(path);
        if (full == directory) throw new MemoryException($"Error: cannot delete {Root} itself");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        else throw new MemoryException($"Error: The path {path} does not exist");
        return $"Successfully deleted {path}";
    }

    string Rename(string oldPath, string newPath)
    {
        string from = Resolve(oldPath), to = Resolve(newPath);
        if (from == directory) throw new MemoryException($"Error: cannot rename {Root} itself");
        if (!File.Exists(from) && !Directory.Exists(from)) throw new MemoryException($"Error: The path {oldPath} does not exist");
        if (File.Exists(to) || Directory.Exists(to)) throw new MemoryException($"Error: The destination {newPath} already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
        return $"Successfully renamed {oldPath} to {newPath}";
    }

    /// <summary>
    /// Maps "/memories/a/b.md" to a real path inside the folder, or refuses. Traversal is refused
    /// by its parts, before the OS gets to interpret them: "..", hidden names, backslashes,
    /// drive letters and percent-escapes never reach Path.GetFullPath.
    /// </summary>
    string Resolve(string path)
    {
        if (path != Root && !path.StartsWith(Root + "/", StringComparison.Ordinal))
            throw new MemoryException($"Error: {path} is outside {Root}");

        string relative = path[Root.Length..];
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith('.') || part.IndexOfAny(['\\', ':', '%', '\0']) >= 0)
                throw new MemoryException($"Error: {path} is not an allowed path");
        }

        string full = Path.GetFullPath(Path.Combine(directory, relative.TrimStart('/')));
        string withSeparator = DevTools.WithSeparator(full);
        if (!withSeparator.StartsWith(directory, PathComparison))
            throw new MemoryException($"Error: {path} is outside {Root}");
        return Directory.Exists(full) ? withSeparator : full;
    }

    string ExistingFile(string path, string missing)
    {
        string full = Resolve(path);
        if (!File.Exists(full)) throw new MemoryException(missing);
        return full;
    }

    /// <summary>Checks the caps and the secret filter, then replaces the file atomically.</summary>
    void Write(string path, string full, string text)
    {
        if (LooksLikeSecret().IsMatch(text))
            throw new MemoryException($"Not written: {path} would contain something shaped like a credential. Never store secrets in memory.");
        if (text.Length > MaxFileChars)
            throw new MemoryException($"Not written: {path} would be {text.Length:N0} characters, over the {MaxFileChars:N0} limit. Merge or delete entries that are no longer true, then try again.");

        long others = Files().Where(f => !string.Equals(f, full, PathComparison)).Sum(f => (long)File.ReadAllText(f).Length);
        if (others + text.Length > MaxTotalChars)
            throw new MemoryException($"Not written: memory would total {others + text.Length:N0} characters, over the {MaxTotalChars:N0} limit. Merge or delete entries that are no longer true, then try again.");

        // Write beside the target and move over it: a crash mid-write leaves the old file, not half a new one.
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}");
        using (FileStream file = new(temp, FileMode.CreateNew, FileAccess.Write))
        {
            file.Write(Encoding.UTF8.GetBytes(text));
            file.Flush(flushToDisk: true);   // on disk before the move, so a power cut can't leave an empty file
        }
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// Two sessions on the same repository (one spoken, one typed) can edit the same file, and
    /// str_replace is read-modify-write. Each command holds an exclusive lock file while it runs,
    /// so no command sees half of another's write. It does not merge the two sessions: each model
    /// writes from what it last read, and a create replaces whatever the other saved since.
    /// </summary>
    FileStream Lock()
    {
        string path = Path.Combine(directory, ".lock");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (DirectoryNotFoundException) when (attempt < 40)
            {
                // The folder was deleted mid-session to make the agent forget: start an empty
                // store instead of reporting the memory as busy.
                Directory.CreateDirectory(directory);
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                throw new MemoryException("Error: memory is busy in another session; try again");
            }
        }
    }

    IEnumerable<string> Files() => Visible(directory, depth: int.MaxValue).Where(File.Exists);

    IEnumerable<string> Visible(string folder, int depth = 1) =>
        Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = depth,
            AttributesToSkip = 0,
        }).Where(entry => !Path.GetRelativePath(directory, entry).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')));

    string ToVirtual(string full) =>
        Root + "/" + Path.GetRelativePath(directory, full).Replace(Path.DirectorySeparatorChar, '/');

    static string[] Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    static string Size(long bytes) => bytes < 1024
        ? $"{bytes}B"
        : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";

    static string Text(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new MemoryException($"Error: missing `{name}`");

    static string OptionalText(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    static int Number(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)
            ? n
            : throw new MemoryException($"Error: missing `{name}`");

    // Shapes of the credentials a developer's session is most likely to mention: API keys and
    // tokens, cloud keys, JWTs, PEM keys, and the password in a connection string, the most
    // likely secret in a .NET project. A denylist is never complete; it catches the common ones.
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{10,}|sk_live_[A-Za-z0-9]{10,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_\w{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?i:\b(password|pwd)\s*=\s*[^;\s]+)")]
    private static partial Regex LooksLikeSecret();
}

sealed class MemoryException(string message) : Exception(message);
