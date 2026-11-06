using System.Text.Json;

public sealed class MemoryStoreTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "memory-tests-" + Guid.NewGuid().ToString("N"));
    readonly MemoryStore store;

    public MemoryStoreTests() => store = new MemoryStore(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    (string Content, bool IsError) Run(object command) =>
        store.Run(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(command))!);

    [Fact]
    public void Create_then_view_returns_numbered_lines()
    {
        Assert.False(Run(new { command = "create", path = "/memories/project.md", file_text = "Core matters.\nCS1591 is noise.\n" }).IsError);

        (string content, bool isError) = Run(new { command = "view", path = "/memories/project.md" });

        Assert.False(isError);
        Assert.Equal("Here's the content of /memories/project.md with line numbers:\n     1\tCore matters.\n     2\tCS1591 is noise.", content);
    }

    [Fact]
    public void Empty_store_lists_only_the_root()
    {
        (string content, bool isError) = Run(new { command = "view", path = "/memories" });

        Assert.False(isError);
        Assert.EndsWith("0B\t/memories", content);
    }

    [Theory]
    [InlineData("/memories/../secrets.env")]
    [InlineData("/memories/..\\secrets.env")]
    [InlineData("/memories/%2e%2e/secrets.env")]
    [InlineData("/memories/C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("/memoriesX/a.md")]
    [InlineData("/memories/.lock")]
    public void Paths_outside_the_store_are_refused(string path)
    {
        (_, bool isError) = Run(new { command = "create", path, file_text = "x" });

        Assert.True(isError);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "secrets.env")));
    }

    [Fact]
    public void Str_replace_refuses_an_ambiguous_match_and_names_the_lines()
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "branch: main\nnotes\nbranch: main\n" });

        (string content, bool isError) = Run(new { command = "str_replace", path = "/memories/project.md", old_str = "branch: main", new_str = "branch: dev" });

        Assert.True(isError);
        Assert.Contains("in lines: 1, 3", content);
    }

    [Fact]
    public void Str_replace_edits_a_unique_match()
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "Working on: rename Patch to Diff\n" });

        Assert.False(Run(new { command = "str_replace", path = "/memories/project.md", old_str = "rename Patch to Diff", new_str = "done" }).IsError);
        Assert.Equal("Working on: done\n", File.ReadAllText(Path.Combine(folder, "project.md")));
    }

    [Fact]
    public void Insert_at_zero_puts_text_first()
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "second\n" });

        Run(new { command = "insert", path = "/memories/project.md", insert_line = 0, insert_text = "first\n" });

        Assert.Equal("first\nsecond\n", File.ReadAllText(Path.Combine(folder, "project.md")));
    }

    [Fact]
    public void Rename_never_overwrites()
    {
        Run(new { command = "create", path = "/memories/a.md", file_text = "a" });
        Run(new { command = "create", path = "/memories/b.md", file_text = "b" });

        (string content, bool isError) = Run(new { command = "rename", old_path = "/memories/a.md", new_path = "/memories/b.md" });

        Assert.True(isError);
        Assert.Contains("already exists", content);
        Assert.Equal("b", File.ReadAllText(Path.Combine(folder, "b.md")));
    }

    [Fact]
    public void The_root_cannot_be_deleted()
    {
        Assert.True(Run(new { command = "delete", path = "/memories" }).IsError);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void A_file_over_the_cap_is_refused_and_the_old_one_kept()
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "short\n" });

        (string content, bool isError) = Run(new { command = "create", path = "/memories/project.md", file_text = new string('x', 4_001) });

        Assert.True(isError);
        Assert.Contains("Merge or delete", content);
        Assert.Equal("short\n", File.ReadAllText(Path.Combine(folder, "project.md")));
    }

    [Fact]
    public void The_total_cap_counts_every_file()
    {
        Run(new { command = "create", path = "/memories/a.md", file_text = new string('a', 3_500) });

        (string content, bool isError) = Run(new { command = "create", path = "/memories/b.md", file_text = new string('b', 3_000) });

        Assert.True(isError);
        Assert.Contains("total", content);
    }

    [Theory]
    [InlineData("""{"command":"view","path":"/memories/project.md","view_range":[1.5,10]}""")]
    [InlineData("""{"command":"view","path":"/memories/project.md","view_range":[1,1e10]}""")]
    [InlineData("""{"command":"view","path":"/memories/project.md","view_range":"1-10"}""")]
    [InlineData("""{"command":"view","path":"/memories/project.md","view_range":[1]}""")]
    public void A_malformed_view_range_is_an_error_not_a_crash(string json)
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "a\nb\n" });

        (string content, bool isError) = store.Run(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

        Assert.True(isError);
        Assert.Contains("view_range", content);
    }

    [Fact]
    public void A_temp_file_left_by_a_crash_is_removed_on_open()
    {
        File.WriteAllText(Path.Combine(folder, ".tmp-orphan"), "half a write");

        _ = new MemoryStore(folder);

        Assert.False(File.Exists(Path.Combine(folder, ".tmp-orphan")));
    }

    [Fact]
    public void A_folder_deleted_mid_session_starts_an_empty_store()
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "old\n" });
        Directory.Delete(folder, recursive: true);

        (string content, bool isError) = Run(new { command = "create", path = "/memories/project.md", file_text = "new\n" });

        Assert.False(isError, content);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(folder, "project.md")));
    }

    // Built at run time, so the source never holds a string a secret scanner would flag. Written
    // as literals, the Google-shaped one opened a GitHub secret-scanning alert on a fake key.
    public static TheoryData<string> CredentialShapes => new()
    {
        "key: sk-ant-" + new string('x', 20),
        "token ghp_" + new string('x', 30),
        "-----BEGIN RSA PRIVATE" + " KEY-----",
        "Server=db;User Id=sa;Password=" + "not-a-real-one;",
        "slack xoxb-" + new string('1', 12),
        "stripe sk_live_" + new string('x', 16),
        "google AIza" + new string('x', 35),
        "bearer eyJ" + new string('x', 12) + ".eyJ" + new string('y', 12) + ".sig",
    };

    [Theory]
    [MemberData(nameof(CredentialShapes))]
    public void Credentials_are_never_written(string text)
    {
        (_, bool isError) = Run(new { command = "create", path = "/memories/project.md", file_text = text });

        Assert.True(isError);
        Assert.False(File.Exists(Path.Combine(folder, "project.md")));
    }

    [Fact]
    public void Snapshot_wraps_each_file_with_its_virtual_path()
    {
        Run(new { command = "create", path = "/memories/project.md", file_text = "Core matters.\n" });

        Assert.Equal("<memory path=\"/memories/project.md\">\nCore matters.\n</memory>\n", store.Snapshot());
    }

    [Fact]
    public void Unknown_commands_and_missing_fields_are_errors_not_exceptions()
    {
        Assert.True(Run(new { command = "chmod", path = "/memories/a.md" }).IsError);
        Assert.True(Run(new { command = "create", path = "/memories/a.md" }).IsError);
    }
}

public sealed class ReadFileTests : IDisposable
{
    readonly string repo = Path.Combine(Path.GetTempPath(), "read-file-tests-" + Guid.NewGuid().ToString("N"));

    public ReadFileTests()
    {
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllLines(Path.Combine(repo, "src", "Orchestrator.cs"), Enumerable.Range(1, 250).Select(i => $"// line {i}"));
        File.WriteAllText(Path.Combine(repo, ".env"), "API_KEY=x");
    }

    public void Dispose() => Directory.Delete(repo, recursive: true);

    [Fact]
    public void Reads_a_numbered_range()
    {
        (string content, bool isError) = DevTools.ReadFile(repo, "src/Orchestrator.cs", 110, 112);

        Assert.False(isError);
        Assert.Equal("src/Orchestrator.cs, lines 110-112 of 250\n  110  // line 110\n  111  // line 111\n  112  // line 112\n", content);
    }

    [Fact]
    public void Caps_a_read_at_200_lines()
    {
        (string content, _) = DevTools.ReadFile(repo, "src/Orchestrator.cs", null, null);

        Assert.StartsWith("src/Orchestrator.cs, lines 1-200 of 250", content);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".env")]
    [InlineData(".git/config")]
    [InlineData("GIT~1/config")]
    [InlineData("deploy/production.env")]
    [InlineData("keys/id_ed25519")]
    [InlineData("keys/id_ed25519::$DATA")]
    [InlineData(".git::$INDEX_ALLOCATION/config")]
    [InlineData("src/Missing.cs")]
    public void Refuses_what_it_should_not_read(string path) =>
        Assert.True(DevTools.ReadFile(repo, path, null, null).IsError);

    [Fact]
    public void Cuts_a_line_longer_than_the_character_budget()
    {
        File.WriteAllText(Path.Combine(repo, "bundle.min.js"), new string('x', 40_000) + "\n" + new string('y', 40_000));

        (string content, bool isError) = DevTools.ReadFile(repo, "bundle.min.js", null, null);

        Assert.False(isError);
        Assert.StartsWith("bundle.min.js, lines 1-1 of 2\n", content);
        Assert.EndsWith(" [line cut at 16000 characters]\n", content);
        Assert.True(content.Length < 16_100, $"{content.Length} characters");
    }

    [Fact]
    public void Stops_before_the_line_that_would_pass_the_character_budget()
    {
        File.WriteAllLines(Path.Combine(repo, "wide.txt"), Enumerable.Repeat(new string('w', 100), 200));

        (string content, _) = DevTools.ReadFile(repo, "wide.txt", null, null);

        Assert.StartsWith("wide.txt, lines 1-148 of 200\n", content);
    }

    [Fact]
    public void Refuses_a_path_through_a_linked_folder()
    {
        string outside = Path.Combine(Path.GetTempPath(), "read-file-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not yours");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(repo, "vendor"), outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;   // Windows without Developer Mode can't create symlinks; nothing to test here.
            }

            (string content, bool isError) = DevTools.ReadFile(repo, "vendor/secret.txt", null, null);

            Assert.True(isError);
            Assert.Contains("link", content);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
