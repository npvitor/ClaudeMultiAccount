using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClaudeMultiAccount.Core;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class SessionNameTests
{
    const string Id = "a7bcd96b-645e-4e6b-88e2-7ed487d69873";
    // What Claude Code makes of a very deep folder: cut at 200 characters plus "-<hash>".
    const string LongDir = "C--" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "-1k2j3h";

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("projects/C--Git-RestoreDB/" + Id + ".jsonl")]
    [InlineData("projects/C--Git-RestoreDB/" + Id + "/custom-title.json")]
    [InlineData("projects/C--Git-RestoreDB/" + Id + "/subagents/agent-1.jsonl")]
    [InlineData("file-history/" + Id + "/2d62c2a1a85be991@v2")]
    [InlineData("projects/")]
    [InlineData("projects/C--Git-RestoreDB/")]
    [InlineData("projects/C--Git-RestoreDB/" + Id + "/")]
    [InlineData("file-history/")]
    [InlineData("file-history/" + Id + "/")]
    [InlineData("projects/" + LongDir + "/" + Id + ".jsonl")]
    public void Allowed_entries(string name) => Assert.True(SessionTransfer.IsAllowedEntry(name));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("projects/../../evil.txt")]
    [InlineData("projects/C--Git/../" + Id + ".jsonl")]
    [InlineData("projects/C--Git/" + Id + "/../../../settings.json")]
    [InlineData("projects/C--Git/not-a-session.jsonl")]
    [InlineData("projects/C--Git/" + Id + ".txt")]
    [InlineData("projects/C:/" + Id + ".jsonl")]
    [InlineData("projects/C--Git/" + Id + "/CON")]
    [InlineData("projects/C--Git/" + Id + "/x./y")]
    [InlineData("projects//" + Id + ".jsonl")]
    [InlineData("settings.json")]
    [InlineData(".credentials.json")]
    [InlineData("file-history/not-an-id/x")]
    [InlineData("file-history/" + Id)]
    [InlineData("/projects/C--Git/" + Id + ".jsonl")]
    [InlineData("projects\\C--Git\\" + Id + ".jsonl")]
    [InlineData("projects/C--Git/" + Id + ".jsonl/")]
    [InlineData("other/")]
    public void Refused_entries(string name) => Assert.False(SessionTransfer.IsAllowedEntry(name));

    [Fact]
    public void Clean_drops_tags_and_shortens()
    {
        Assert.Equal("fix the bug", SessionTransfer.Clean("<pasted_content id=\"1\">\n  fix   the bug\n</pasted_content>"));
        Assert.Null(SessionTransfer.Clean("  <x> </x> "));
        var longText = SessionTransfer.Clean(new string('a', 200))!;
        Assert.Equal(90, longText.Length);
        Assert.EndsWith("…", longText);
    }

    [Fact]
    public void Head_gives_folder_title_and_first_prompt()
    {
        var head = Lines(
            """{"type":"queue-operation","content":"ignored"}""",
            """{"type":"user","isMeta":true,"cwd":"C:\\Git\\App","message":{"role":"user","content":"meta"}}""",
            """{"type":"user","cwd":"C:\\Git\\Other","message":{"role":"user","content":[{"type":"text","text":"Olá, João"}]}}""",
            """{"type":"custom-title","customTitle":"My title"}""");
        var (cwd, prompt, title) = SessionTransfer.ParseHead(head, truncated: false);
        Assert.Equal(@"C:\Git\App", cwd);
        Assert.Equal("Olá, João", prompt);
        Assert.Equal("My title", title);
    }

    [Fact]
    public void Cut_head_still_gives_the_start_of_a_long_prompt()
    {
        var line = """{"parentUuid":null,"type":"user","message":{"role":"user","content":"Revise o \"RepairService\"\nagora""" + new string('x', 5000);
        var (cwd, prompt, _) = SessionTransfer.ParseHead(Encoding.UTF8.GetBytes(line[..300]), truncated: true);
        Assert.Null(cwd);
        Assert.StartsWith("Revise o \"RepairService\" agora", prompt);
    }

    static byte[] Lines(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
}

/// <summary>Runs the real scripts as the current user against fake .claude folders.</summary>
public class SessionTransferTests : IDisposable
{
    const string Dir = "C--Git-App";
    const string IdA = "11111111-1111-4111-8111-111111111111";
    const string IdB = "22222222-2222-4222-8222-222222222222";

    readonly TempDir _temp = new();
    readonly SessionHost _from, _to;

    public SessionTransferTests()
    {
        Directory.CreateDirectory(_temp.File("work"));
        _from = new SessionHost(_temp.File("work"), ScriptRunner.RunCurrentUser, _temp.File("from"));
        _to = new SessionHost(_temp.File("work"), ScriptRunner.RunCurrentUser, _temp.File("to"));
    }

    public void Dispose() => _temp.Dispose();

    string Transcript(string home, string id) => Path.Combine(_temp.File(home), "projects", Dir, id + ".jsonl");

    void WriteSession(string home, string id, string prompt, bool extras = true)
    {
        var file = Transcript(home, id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,
            $$$"""{"type":"user","cwd":"C:\\Git\\App","message":{"role":"user","content":"{{{prompt}}}"}}""" + "\n");
        if (!extras)
            return;
        var side = Path.Combine(Path.GetDirectoryName(file)!, id);
        Directory.CreateDirectory(Path.Combine(side, "subagents"));
        File.WriteAllText(Path.Combine(side, "custom-title.json"), """{"customTitle":"Título ✓"}""");
        File.WriteAllText(Path.Combine(side, "subagents", "agent-1.jsonl"), "{}\n");
        var history = Path.Combine(_temp.File(home), "file-history", id);
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "abc@v1"), "backup");
    }

    void Append(string home, string id, string line) => File.AppendAllText(Transcript(home, id), line + "\n");

    ImportResult CopyAll()
    {
        var sessions = SessionTransfer.List(_from);
        return SessionTransfer.Copy(_from, sessions, "Origem", _to);
    }

    [Fact]
    public void Lists_sessions_with_title_folder_and_size()
    {
        WriteSession("from", IdA, "primeiro pedido");
        WriteSession("from", IdB, "segundo pedido", extras: false);
        var sessions = SessionTransfer.List(_from);

        Assert.Equal(2, sessions.Count);
        var a = sessions.Single(s => s.Id == IdA);
        Assert.Equal("Título ✓", a.Title);
        Assert.Equal(@"C:\Git\App", a.Cwd);
        Assert.Equal(Dir, a.ProjectDir);
        Assert.Equal(new FileInfo(Transcript("from", IdA)).Length, a.Size);
        Assert.Equal("segundo pedido", sessions.Single(s => s.Id == IdB).Title);
    }

    [Fact]
    public void Listing_an_account_without_sessions_is_empty() =>
        Assert.Empty(SessionTransfer.List(_to));

    [Fact]
    public void Export_then_import_copies_transcript_and_side_files()
    {
        WriteSession("from", IdA, "pedido");
        var zip = _temp.File("out.zip");
        SessionTransfer.Export(_from, SessionTransfer.List(_from), "Pessoal", zip);

        var manifest = SessionTransfer.ReadManifest(zip);
        Assert.Equal("Pessoal", manifest.Source);
        Assert.Equal(IdA, Assert.Single(manifest.Sessions).Id);

        var result = SessionTransfer.Import(_to, zip);
        Assert.Equal(ImportState.New, Assert.Single(result.Sessions).State);
        Assert.Equal(File.ReadAllText(Transcript("from", IdA)), File.ReadAllText(Transcript("to", IdA)));
        Assert.True(File.Exists(Path.Combine(_temp.File("to"), "projects", Dir, IdA, "subagents", "agent-1.jsonl")));
        Assert.Equal("backup", File.ReadAllText(Path.Combine(_temp.File("to"), "file-history", IdA, "abc@v1")));
        Assert.Equal(new[] { @"C:\Git\App" }, result.WrittenFolders);
        // Nothing is left behind in the exchange folder.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.File("work")));
    }

    [Fact]
    public void Importing_again_leaves_it_unchanged()
    {
        WriteSession("from", IdA, "pedido");
        CopyAll();
        Assert.Equal(ImportState.Unchanged, Assert.Single(CopyAll().Sessions).State);
    }

    [Fact]
    public void A_continued_conversation_replaces_the_older_copy()
    {
        WriteSession("from", IdA, "pedido");
        CopyAll();
        Append("from", IdA, """{"type":"assistant"}""");

        Assert.Equal(ImportState.Updated, Assert.Single(CopyAll().Sessions).State);
        Assert.Equal(File.ReadAllText(Transcript("from", IdA)), File.ReadAllText(Transcript("to", IdA)));
    }

    [Fact]
    public void A_newer_copy_in_the_destination_is_kept()
    {
        WriteSession("from", IdA, "pedido");
        CopyAll();
        Append("to", IdA, """{"type":"assistant","more":1}""");
        var before = File.ReadAllText(Transcript("to", IdA));

        Assert.Equal(ImportState.KeptNewer, Assert.Single(CopyAll().Sessions).State);
        Assert.Equal(before, File.ReadAllText(Transcript("to", IdA)));
    }

    [Fact]
    public void Diverged_conversations_keep_the_destination_copy()
    {
        WriteSession("from", IdA, "pedido");
        CopyAll();
        Append("from", IdA, """{"type":"assistant","from":"a"}""");
        Append("to", IdA, """{"type":"assistant","from":"b"}""");
        var before = File.ReadAllText(Transcript("to", IdA));

        Assert.Equal(ImportState.Conflict, Assert.Single(CopyAll().Sessions).State);
        Assert.Equal(before, File.ReadAllText(Transcript("to", IdA)));
    }

    [Fact]
    public void Archive_with_a_path_outside_the_session_folders_is_refused_whole()
    {
        var zip = _temp.File("evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                w.Write("""{"Format":"claude-multi-account-sessions","Version":1,"Sessions":[]}""");
            using (var w = new StreamWriter(archive.CreateEntry($"projects/{Dir}/{IdA}.jsonl").Open()))
                w.Write("{}\n");
            using (var w = new StreamWriter(archive.CreateEntry("projects/../../evil.txt").Open()))
                w.Write("x");
        }

        Assert.Throws<LauncherException>(() => SessionTransfer.Import(_to, zip));
        Assert.False(Directory.Exists(_temp.File("to")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.File("work")));
    }

    [Fact]
    public void A_rename_at_the_end_of_a_long_transcript_is_the_title()
    {
        WriteSession("from", IdA, "pedido", extras: false);
        for (var i = 0; i < 400; i++)
            Append("from", IdA, $$"""{"type":"assistant","n":{{i}},"text":"{{new string('x', 100)}}"}""");
        Append("from", IdA, """{"type":"custom-title","customTitle":"Nome novo"}""");

        Assert.Equal("Nome novo", Assert.Single(SessionTransfer.List(_from)).Title);
    }

    static string MakeZip(string path, params string[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
            w.Write("""{"Format":"claude-multi-account-sessions","Version":1,"Sessions":[]}""");
        foreach (var name in entries)
        {
            var entry = archive.CreateEntry(name);
            if (!name.EndsWith('/'))
                using (var w = new StreamWriter(entry.Open()))
                    w.Write("{}\n");
        }
        return path;
    }

    [Fact]
    public void Folder_entries_from_other_zip_tools_are_accepted()
    {
        var zip = MakeZip(_temp.File("rezipped.zip"), "projects/", $"projects/{Dir}/", $"projects/{Dir}/{IdA}.jsonl");
        Assert.Equal(ImportState.New, Assert.Single(SessionTransfer.Import(_to, zip).Sessions).State);
    }

    [Fact]
    public void Entries_that_differ_only_in_case_are_refused()
    {
        var zip = MakeZip(_temp.File("dupes.zip"), $"projects/{Dir}/{IdA}.jsonl", $"projects/{Dir.ToLowerInvariant()}/{IdA}.jsonl");
        Assert.Throws<LauncherException>(() => SessionTransfer.Import(_to, zip));
        Assert.False(Directory.Exists(_temp.File("to")));
    }

    [Fact]
    public void Archive_without_manifest_is_refused()
    {
        var zip = _temp.File("plain.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry($"projects/{Dir}/{IdA}.jsonl");
        Assert.Throws<LauncherException>(() => SessionTransfer.ReadManifest(zip));
        Assert.Throws<LauncherException>(() => SessionTransfer.Import(_to, zip));
    }

    [Fact]
    public void Not_a_zip_is_refused_with_a_friendly_error()
    {
        var file = _temp.File("notes.zip");
        File.WriteAllText(file, "hello");
        Assert.Throws<LauncherException>(() => SessionTransfer.ReadManifest(file));
    }
}

public class ImportSummaryTests
{
    static ImportResult Result(params (string Id, string? Cwd, ImportState State)[] sessions) => new(
        new SessionManifest { Sessions = sessions.Select(s => new SessionInfo { Id = s.Id, Cwd = s.Cwd }).ToList() },
        sessions.Select(s => new ImportedSession { Id = s.Id, State = s.State }).ToList());

    [Theory]
    [InlineData(@"C:\Git\App", @"C:\Git", true)]
    [InlineData(@"C:\Git", @"C:\Git\", true)]
    [InlineData(@"c:\git\app\src", @"C:\Git", true)]
    [InlineData(@"C:\Gitlab\App", @"C:\Git", false)]
    [InlineData(@"D:\Code", @"C:\", false)]
    [InlineData(@"C:\Code", @"C:\", true)]
    public void IsUnder(string path, string folder, bool expected) =>
        Assert.Equal(expected, UI.SessionsWindow.IsUnder(path, folder));

    [Fact]
    public void Summary_counts_states_and_lists_folders_the_profile_cannot_reach()
    {
        using var _ = new CultureScope("en-US");
        var profile = new Profile { DisplayName = "Work", SharedFolders = { @"C:\Git" } };
        var text = UI.SessionsWindow.DescribeImport(Result(
            ("a", @"C:\Git\App", ImportState.New),
            ("b", @"D:\Other", ImportState.Updated),
            ("c", @"E:\Skipped", ImportState.Conflict)), new UI.ProfileCard(profile));

        Assert.Contains("1 new", text);
        Assert.Contains("1 updated", text);
        Assert.Contains("1 not imported", text);
        Assert.DoesNotContain("unchanged", text);
        Assert.Contains(@"D:\Other", text);
        Assert.DoesNotContain(@"C:\Git\App", text);
        Assert.DoesNotContain(@"E:\Skipped", text); // not written, so no need to reach it
        Assert.Contains("/resume", text);
    }

    [Fact]
    public void Main_account_summary_has_no_folder_warning()
    {
        using var _ = new CultureScope("en-US");
        var text = UI.SessionsWindow.DescribeImport(Result(("a", @"Z:\Anywhere", ImportState.New)), new UI.ProfileCard(null));
        Assert.DoesNotContain(@"Z:\Anywhere", text);
    }

    [Fact]
    public void Nothing_written_means_no_resume_hint()
    {
        using var _ = new CultureScope("en-US");
        var text = UI.SessionsWindow.DescribeImport(Result(("a", null, ImportState.Unchanged)), new UI.ProfileCard(null));
        Assert.Contains("1 already here", text);
        Assert.DoesNotContain("/resume", text);
    }
}
