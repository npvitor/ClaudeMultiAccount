using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

/// <summary>One Claude Code conversation: <c>.claude\projects\&lt;ProjectDir&gt;\&lt;Id&gt;.jsonl</c> plus its side files.</summary>
public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    /// <summary>Claude Code's folder name for the project (the working folder with every symbol turned into "-").</summary>
    public string ProjectDir { get; set; } = "";
    /// <summary>The folder the conversation ran in, when the transcript says so.</summary>
    public string? Cwd { get; set; }
    public string Title { get; set; } = "";
    public DateTime ModifiedUtc { get; set; }
    public long Size { get; set; }
}

/// <summary>The <c>manifest.json</c> at the root of an exported sessions file.</summary>
public sealed class SessionManifest
{
    public const string FormatName = "claude-multi-account-sessions";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public DateTime ExportedAtUtc { get; set; }
    /// <summary>Name of the account the sessions came from (for the import confirmation only).</summary>
    public string Source { get; set; } = "";
    public List<SessionInfo> Sessions { get; set; } = new();
}

public enum ImportState
{
    /// <summary>The account didn't have it.</summary>
    New,
    /// <summary>The account had an older copy: the imported one continues it, so it replaced it.</summary>
    Updated,
    /// <summary>The account already had exactly this conversation.</summary>
    Unchanged,
    /// <summary>The account's copy continues the imported one, so it was kept.</summary>
    KeptNewer,
    /// <summary>The conversation went on differently in both accounts; the account's copy was kept.</summary>
    Conflict,
}

public sealed class ImportedSession
{
    public string Id { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public ImportState State { get; set; }
}

public sealed class ImportResult
{
    public ImportResult(SessionManifest manifest, IReadOnlyList<ImportedSession> sessions)
    {
        Manifest = manifest;
        Sessions = sessions;
    }

    public SessionManifest Manifest { get; }
    public IReadOnlyList<ImportedSession> Sessions { get; }

    public int Count(ImportState state) => Sessions.Count(s => s.State == state);

    /// <summary>Working folders of the sessions that were written to the account.</summary>
    public IEnumerable<string> WrittenFolders =>
        Sessions.Where(s => s.State is ImportState.New or ImportState.Updated)
            .Select(s => Manifest.Sessions.FirstOrDefault(m => m.Id == s.Id)?.Cwd)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The account whose Claude Code sessions are read or written. Its files are only readable by that Windows
/// user, so the file work runs as a script under that user; <see cref="WorkFolder"/> is where the script and
/// the launcher's user exchange files (the profile's locked-down work folder, or the main account's temp).
/// </summary>
public sealed class SessionHost
{
    internal SessionHost(string workFolder, Action<string, string, TimeSpan> runScript, string? claudeHome = null)
    {
        WorkFolder = workFolder;
        RunScript = runScript;
        ClaudeHome = claudeHome;
    }

    internal string WorkFolder { get; }
    /// <summary>(step name, PowerShell body, timeout).</summary>
    internal Action<string, string, TimeSpan> RunScript { get; }
    /// <summary>Claude's data folder; null means the user's own (CLAUDE_CONFIG_DIR, or %USERPROFILE%\.claude).</summary>
    internal string? ClaudeHome { get; }

    /// <summary>A profile, or the main account when <paramref name="profile"/> is null.</summary>
    public static SessionHost For(Profile? profile)
    {
        if (profile is null)
            return new SessionHost(Paths.Temp, ScriptRunner.RunCurrentUser);
        var password = Secret.Unprotect(profile.ProtectedPassword);
        return new SessionHost(Paths.RequireProfileWork(profile.WindowsUser),
            (step, body, timeout) => ScriptRunner.Run(profile, password, step, body, timeout));
    }
}

/// <summary>Lists, exports and imports Claude Code sessions, so a conversation can go on in another account.</summary>
public static class SessionTransfer
{
    static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(30);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Bytes of each transcript read for the listing: enough for the first prompt and the folder.</summary>
    internal const int HeadBytes = 32 * 1024;
    /// <summary>Refuses archives that would unpack to more than this (a zip bomb, not a real export).</summary>
    internal const long MaxUnpackedBytes = 20L * 1024 * 1024 * 1024;

    public static IReadOnlyList<SessionInfo> List(SessionHost host)
    {
        var output = Path.Combine(host.WorkFolder, $"sessions-{Guid.NewGuid():N}.json");
        try
        {
            host.RunScript("list-sessions", BuildListScript(host.ClaudeHome, output), ListTimeout);
            return ParseListing(File.ReadAllText(output));
        }
        finally
        {
            Paths.TryDelete(output);
        }
    }

    /// <summary>Writes <paramref name="sessions"/> (from <see cref="List"/>) to a .zip file.</summary>
    public static void Export(SessionHost host, IReadOnlyList<SessionInfo> sessions, string sourceName, string zipPath) =>
        Export(host, sessions, sourceName, zipPath, CompressionLevel.Optimal);

    static void Export(SessionHost host, IReadOnlyList<SessionInfo> sessions, string sourceName, string zipPath, CompressionLevel level)
    {
        if (sessions.Count == 0)
            throw new LauncherException(S.ErrNoSessionsSelected);
        foreach (var session in sessions)
            if (!IsSessionId(session.Id) || !IsProjectDir(session.ProjectDir))
                throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, session.Id));

        var stage = Path.Combine(host.WorkFolder, "x" + ShortId());
        try
        {
            host.RunScript("export-sessions", BuildExportScript(host.ClaudeHome, stage, sessions), CopyTimeout);
            var manifest = new SessionManifest { ExportedAtUtc = DateTime.UtcNow, Source = sourceName, Sessions = sessions.ToList() };
            WriteArchive(stage, manifest, zipPath, level);
        }
        finally
        {
            TryDeleteFolder(stage);
        }
    }

    /// <summary>Reads only the manifest, to confirm with the user before importing.</summary>
    public static SessionManifest ReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return ReadManifest(zip);
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, ex.Message));
        }
    }

    /// <summary>
    /// Copies the sessions of an exported file into the account. A session the account already has is only
    /// replaced when the imported copy continues it; nothing the account has is ever lost.
    /// </summary>
    public static ImportResult Import(SessionHost host, string zipPath)
    {
        var id = ShortId();
        var stage = Path.Combine(host.WorkFolder, "i" + id);
        var output = Path.Combine(host.WorkFolder, $"i{id}.json");
        try
        {
            var manifest = ExtractArchive(zipPath, stage);
            host.RunScript("import-sessions", BuildImportScript(host.ClaudeHome, stage, output), CopyTimeout);
            var sessions = JsonSerializer.Deserialize<List<ImportedSession>>(File.ReadAllText(output), new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }) ?? new();
            return new ImportResult(manifest, sessions);
        }
        finally
        {
            TryDeleteFolder(stage);
            Paths.TryDelete(output);
        }
    }

    /// <summary>Exports from one account and imports into another in one go.</summary>
    public static ImportResult Copy(SessionHost from, IReadOnlyList<SessionInfo> sessions, string sourceName, SessionHost to)
    {
        var zip = Path.Combine(Paths.Temp, $"transfer-{Guid.NewGuid():N}.zip");
        try
        {
            // The file only lives for a moment: compressing hard would just cost time.
            Export(from, sessions, sourceName, zip, CompressionLevel.Fastest);
            return Import(to, zip);
        }
        finally
        {
            Paths.TryDelete(zip);
        }
    }

    /// <summary>Staging folders get short names: the scripts run on Windows PowerShell, where paths stop at 260 characters.</summary>
    static string ShortId() => Guid.NewGuid().ToString("N")[..8];

    // ---- Names ----

    static readonly Regex SessionIdPattern = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    // Claude Code cuts long folder names at 200 characters and adds "-<hash>"; 255 is NTFS's limit.
    static readonly Regex ProjectDirPattern = new("^[A-Za-z0-9._-]{1,255}$");
    static readonly Regex ReservedName = new(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase);

    internal static bool IsSessionId(string? id) => id is not null && SessionIdPattern.IsMatch(id);

    internal static bool IsProjectDir(string? dir) => dir is not null && ProjectDirPattern.IsMatch(dir) && dir.Trim('.').Length > 0;

    /// <summary>
    /// The only paths an exported file may hold. Anything else (another folder of .claude, "..", a drive,
    /// a device name) makes the whole file be refused, so an archive can't write outside the session folders.
    /// </summary>
    internal static bool IsAllowedEntry(string name)
    {
        if (name == "manifest.json")
            return true;
        // Folder entries ("projects/", "file-history/<id>/"), as other zip tools add, are fine on the way to an allowed file.
        var folder = name.EndsWith('/');
        var parts = name.TrimEnd('/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                           p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedName.IsMatch(p)))
            return false;
        return parts[0] switch
        {
            "projects" => parts.Length == 1 ? folder
                : IsProjectDir(parts[1]) && parts.Length switch
                {
                    2 => folder,
                    3 when !folder => parts[2].EndsWith(".jsonl", StringComparison.Ordinal) && IsSessionId(parts[2][..^6]),
                    _ => IsSessionId(parts[2]),
                },
            "file-history" => parts.Length == 1 ? folder : IsSessionId(parts[1]) && (parts.Length >= 3 || folder),
            _ => false,
        };
    }

    // ---- Listing ----

    sealed class RawSession
    {
        public string? Id { get; set; }
        public string? ProjectDir { get; set; }
        public long Size { get; set; }
        public string? Modified { get; set; }
        public string? Head { get; set; }
        /// <summary>The last bytes of a transcript longer than the head (starts mid-line).</summary>
        public string? Tail { get; set; }
        public string? TitleJson { get; set; }
    }

    static byte[] FromBase64(string? text)
    {
        try { return Convert.FromBase64String(text ?? ""); }
        catch (FormatException) { return Array.Empty<byte>(); }
    }

    /// <summary>The latest custom-title in the end of a transcript; the first, cut line is skipped.</summary>
    internal static string? ParseTailTitle(byte[] tail)
    {
        var start = Array.IndexOf(tail, (byte)'\n') + 1;
        return start == 0 ? null : ParseHead(tail[start..], truncated: false).Title;
    }

    internal static List<SessionInfo> ParseListing(string json)
    {
        var raw = JsonSerializer.Deserialize<List<RawSession>>(json) ?? new();
        var sessions = new List<SessionInfo>();
        foreach (var r in raw)
        {
            if (!IsSessionId(r.Id) || !IsProjectDir(r.ProjectDir))
                continue;
            var head = FromBase64(r.Head);
            var (cwd, prompt, lineTitle) = ParseHead(head, truncated: head.Length < r.Size);
            sessions.Add(new SessionInfo
            {
                Id = r.Id!,
                ProjectDir = r.ProjectDir!,
                Cwd = cwd,
                Title = ParseTitleFile(r.TitleJson) ?? ParseTailTitle(FromBase64(r.Tail)) ?? lineTitle ?? prompt ?? "",
                ModifiedUtc = DateTime.TryParse(r.Modified, null, System.Globalization.DateTimeStyles.RoundtripKind, out var m)
                    ? m.ToUniversalTime() : DateTime.MinValue,
                Size = r.Size,
            });
        }
        return sessions.OrderByDescending(s => s.ModifiedUtc).ToList();
    }

    static string? ParseTitleFile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("customTitle", out var t) && t.ValueKind == JsonValueKind.String
                ? Clean(t.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the folder, the title and the first prompt from the start of a transcript. When the start was cut
    /// (<paramref name="truncated"/>), the last line is incomplete and only scanned for the beginning of the prompt.
    /// </summary>
    internal static (string? Cwd, string? Prompt, string? Title) ParseHead(byte[] head, bool truncated)
    {
        var lines = Encoding.UTF8.GetString(head).Split('\n');
        string? cwd = null, prompt = null, title = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            if (truncated && i == lines.Length - 1)
            {
                cwd ??= PartialString(line, "\"cwd\":\"", complete: true);
                if (prompt is null && line.Contains("\"type\":\"user\"", StringComparison.Ordinal) &&
                    !line.Contains("\"isMeta\":true", StringComparison.Ordinal))
                    prompt = Clean(PartialString(line, "\"content\":\"", complete: false));
                break;
            }
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;
                if (cwd is null && root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String)
                    cwd = c.GetString();
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type == "custom-title" && root.TryGetProperty("customTitle", out var ct) && ct.ValueKind == JsonValueKind.String)
                    title = Clean(ct.GetString()) ?? title;
                if (type == "user" && prompt is null && !IsTrue(root, "isMeta") && !IsTrue(root, "isSidechain") &&
                    root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var content))
                    prompt = Clean(TextOf(content));
            }
            catch (JsonException)
            {
            }
        }
        return (cwd, prompt, title);
    }

    static bool IsTrue(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? TextOf(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
        return null;
    }

    /// <summary>
    /// Decodes the JSON string that follows <paramref name="marker"/> in a cut line. With <paramref name="complete"/>
    /// the string must end inside the line; otherwise whatever is there is returned.
    /// </summary>
    internal static string? PartialString(string line, string marker, bool complete)
    {
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var sb = new StringBuilder();
        for (var i = start + marker.Length; i < line.Length && sb.Length < 400; i++)
        {
            var ch = line[i];
            if (ch == '"')
                return sb.ToString();
            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }
            if (++i >= line.Length)
                break;
            switch (line[i])
            {
                case 'n': case 'r': case 't': sb.Append(' '); break;
                case 'u':
                    if (i + 4 >= line.Length || !int.TryParse(line.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                        return complete ? null : sb.ToString();
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(line[i]); break; // \" \\ \/ (and \b \f, harmless)
            }
        }
        return complete ? null : sb.ToString();
    }

    static readonly Regex Tags = new("<[^>]*>");
    static readonly Regex Spaces = new(@"\s+");

    /// <summary>One line for the list: markup tags out, whitespace collapsed, at most ~90 characters.</summary>
    internal static string? Clean(string? text)
    {
        if (text is null)
            return null;
        text = Spaces.Replace(Tags.Replace(text, " "), " ").Trim();
        if (text.Length == 0)
            return null;
        return text.Length <= 90 ? text : text[..89].TrimEnd() + "…";
    }

    // ---- Archive ----

    internal static void WriteArchive(string stage, SessionManifest manifest, string zipPath, CompressionLevel level = CompressionLevel.Optimal)
    {
        var tmp = zipPath + ".tmp";
        Paths.TryDelete(tmp);
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("manifest.json");
                using (var stream = entry.Open())
                    JsonSerializer.Serialize(stream, manifest, Json);

                if (Directory.Exists(stage))
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(stage, "*", options))
                    {
                        var name = Path.GetRelativePath(stage, file).Replace('\\', '/');
                        if (IsAllowedEntry(name))
                            zip.CreateEntryFromFile(file, name, level);
                    }
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            Paths.TryDelete(tmp);
            throw;
        }
    }

    static SessionManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, "manifest.json"));
        SessionManifest? manifest;
        try
        {
            using var stream = entry.Open();
            manifest = JsonSerializer.Deserialize<SessionManifest>(stream);
        }
        catch (JsonException ex)
        {
            throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, ex.Message));
        }
        if (manifest is null || manifest.Format != SessionManifest.FormatName)
            throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, "manifest.json"));
        if (manifest.Version > SessionManifest.CurrentVersion)
            throw new LauncherException(S.ErrSessionArchiveNewer);
        return manifest;
    }

    /// <summary>Checks every entry first, then unpacks into <paramref name="stage"/>. Returns the manifest.</summary>
    internal static SessionManifest ExtractArchive(string zipPath, string stage)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = ReadManifest(zip);

            long total = 0;
            var transcripts = 0;
            // NTFS ignores case: two entries that differ only in it would land on the same file.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (!IsAllowedEntry(entry.FullName) || !names.Add(entry.FullName.TrimEnd('/')))
                    throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, entry.FullName));
                total += entry.Length;
                if (entry.FullName.StartsWith("projects/", StringComparison.Ordinal) && entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal) &&
                    entry.FullName.Count(c => c == '/') == 2)
                    transcripts++;
            }
            if (total > MaxUnpackedBytes)
                throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, "size"));
            if (transcripts == 0)
                throw new LauncherException(S.ErrSessionArchiveEmpty);

            var root = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "manifest.json" || entry.FullName.EndsWith('/'))
                    continue;
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
            return manifest;
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException(string.Format(S.ErrSessionArchiveInvalid, ex.Message));
        }
    }

    static void TryDeleteFolder(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    // ---- Scripts (run as the account's own user, Windows PowerShell 5.1) ----

    /// <summary>Claude's data folder as the account's user sees it.</summary>
    static string RootLine(string? claudeHome) => claudeHome is not null
        ? $"$root = {ScriptRunner.Quote(claudeHome)}"
        : "$root = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }";

    /// <summary>
    /// Copies files opened with full sharing (Claude may be appending to a transcript right now) and keeps
    /// their dates. Links inside the tree are skipped, never followed.
    /// </summary>
    const string CopyFunctions = """
        function Copy-Shared([string]$from, [string]$to) {
            $in = [System.IO.File]::Open($from, 'Open', 'Read', 'ReadWrite, Delete')
            try {
                $out = [System.IO.File]::Create($to)
                try { $in.CopyTo($out) } finally { $out.Dispose() }
            } finally { $in.Dispose() }
            [System.IO.File]::SetLastWriteTimeUtc($to, [System.IO.File]::GetLastWriteTimeUtc($from))
        }
        function Copy-Tree([string]$from, [string]$to) {
            $info = New-Object System.IO.DirectoryInfo $from
            if (-not $info.Exists -or ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { return }
            [void][System.IO.Directory]::CreateDirectory($to)
            foreach ($item in $info.GetFileSystemInfos()) {
                if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
                $target = [System.IO.Path]::Combine($to, $item.Name)
                if ($item -is [System.IO.DirectoryInfo]) { Copy-Tree $item.FullName $target } else { Copy-Shared $item.FullName $target }
            }
        }
        function Write-Utf8([string]$path, [string]$text) {
            [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
        }
        """;

    internal static string BuildListScript(string? claudeHome, string outputPath) => $$"""
        {{RootLine(claudeHome)}}
        {{CopyFunctions}}
        $projects = Join-Path $root 'projects'
        $list = New-Object System.Collections.Generic.List[object]
        if (Test-Path -LiteralPath $projects -PathType Container) {
            foreach ($dir in Get-ChildItem -LiteralPath $projects -Directory -Force -ErrorAction SilentlyContinue) {
                foreach ($file in Get-ChildItem -LiteralPath $dir.FullName -Filter '*.jsonl' -File -Force -ErrorAction SilentlyContinue) {
                    if ($file.Length -eq 0) { continue }
                    try {
                        $stream = [System.IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
                        try {
                            $buffer = New-Object byte[] ([int][Math]::Min($file.Length, {{HeadBytes}}))
                            $read = $stream.Read($buffer, 0, $buffer.Length)
                            $head = [Convert]::ToBase64String($buffer, 0, $read)
                            # A rename is appended when it happens, so the latest title is near the end.
                            $tail = ''
                            if ($file.Length -gt {{HeadBytes}}) {
                                [void]$stream.Seek([Math]::Max({{HeadBytes}}, $file.Length - {{HeadBytes}}), 'Begin')
                                $read = $stream.Read($buffer, 0, $buffer.Length)
                                $tail = [Convert]::ToBase64String($buffer, 0, $read)
                            }
                        } finally { $stream.Dispose() }
                    } catch { continue }
                    $titleFile = [System.IO.Path]::Combine($dir.FullName, $file.BaseName, 'custom-title.json')
                    $title = ''
                    if (Test-Path -LiteralPath $titleFile -PathType Leaf) { try { $title = [System.IO.File]::ReadAllText($titleFile) } catch { } }
                    $list.Add([pscustomobject]@{
                        Id = $file.BaseName; ProjectDir = $dir.Name; Size = $file.Length
                        Modified = $file.LastWriteTimeUtc.ToString('o'); Head = $head; Tail = $tail; TitleJson = $title
                    })
                }
            }
        }
        Write-Utf8 {{ScriptRunner.Quote(outputPath)}} (ConvertTo-Json -InputObject $list.ToArray() -Compress)
        """;

    internal static string BuildExportScript(string? claudeHome, string stage, IEnumerable<SessionInfo> sessions)
    {
        var body = new StringBuilder()
            .AppendLine(RootLine(claudeHome))
            .AppendLine(CopyFunctions)
            .AppendLine($"$stage = {ScriptRunner.Quote(stage)}")
            .AppendLine("""
                function Export-Session([string]$dir, [string]$id) {
                    $from = [System.IO.Path]::Combine($root, 'projects', $dir)
                    $transcript = Join-Path $from "$id.jsonl"
                    if (-not (Test-Path -LiteralPath $transcript -PathType Leaf)) { throw "Session $id was not found in $from." }
                    $to = [System.IO.Path]::Combine($stage, 'projects', $dir)
                    [void][System.IO.Directory]::CreateDirectory($to)
                    Copy-Shared $transcript (Join-Path $to "$id.jsonl")
                    Copy-Tree (Join-Path $from $id) (Join-Path $to $id)
                    Copy-Tree ([System.IO.Path]::Combine($root, 'file-history', $id)) ([System.IO.Path]::Combine($stage, 'file-history', $id))
                }
                """);
        foreach (var s in sessions)
            body.AppendLine($"Export-Session {ScriptRunner.Quote(s.ProjectDir)} {ScriptRunner.Quote(s.Id)}");
        return body.ToString();
    }

    /// <summary>
    /// Transcripts only grow, so comparing the shorter one with the start of the longer one tells which copy
    /// continues the other. Only a continuation replaces what the account has; anything else keeps it.
    /// </summary>
    internal static string BuildImportScript(string? claudeHome, string stage, string outputPath) => $$"""
        {{RootLine(claudeHome)}}
        {{CopyFunctions}}
        $stage = {{ScriptRunner.Quote(stage)}}
        function Get-PrefixHash([string]$path, [long]$length) {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $stream = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
            try {
                $buffer = New-Object byte[] 1048576
                $left = $length
                while ($left -gt 0) {
                    $n = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left))
                    if ($n -le 0) { break }
                    [void]$sha.TransformBlock($buffer, 0, $n, $null, 0)
                    $left -= $n
                }
                [void]$sha.TransformFinalBlock($buffer, 0, 0)
                [Convert]::ToBase64String($sha.Hash)
            } finally { $stream.Dispose(); $sha.Dispose() }
        }
        $results = New-Object System.Collections.Generic.List[object]
        foreach ($dir in Get-ChildItem -LiteralPath (Join-Path $stage 'projects') -Directory) {
            foreach ($file in Get-ChildItem -LiteralPath $dir.FullName -Filter '*.jsonl' -File) {
                $id = $file.BaseName
                $targetDir = [System.IO.Path]::Combine($root, 'projects', $dir.Name)
                $target = Join-Path $targetDir $file.Name
                $state = 'New'
                $existing = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                if ($existing) {
                    $mine = $existing.Length; $theirs = $file.Length
                    if ($theirs -gt $mine -and (Get-PrefixHash $file.FullName $mine) -eq (Get-PrefixHash $target $mine)) { $state = 'Updated' }
                    elseif ($theirs -eq $mine -and (Get-PrefixHash $file.FullName $mine) -eq (Get-PrefixHash $target $mine)) { $state = 'Unchanged' }
                    elseif ($theirs -lt $mine -and (Get-PrefixHash $file.FullName $theirs) -eq (Get-PrefixHash $target $theirs)) { $state = 'KeptNewer' }
                    else { $state = 'Conflict' }
                }
                if ($state -eq 'New' -or $state -eq 'Updated') {
                    [void][System.IO.Directory]::CreateDirectory($targetDir)
                    Copy-Tree (Join-Path $dir.FullName $id) (Join-Path $targetDir $id)
                    Copy-Tree ([System.IO.Path]::Combine($stage, 'file-history', $id)) ([System.IO.Path]::Combine($root, 'file-history', $id))
                    # The transcript goes last and in one step, so Claude never sees half of it.
                    $partial = "$target.importing"
                    Copy-Shared $file.FullName $partial
                    try {
                        if (-not $existing) { [System.IO.File]::Move($partial, $target) }
                        # Claude wrote to it since the check: keep its copy, nothing is lost.
                        elseif ((New-Object System.IO.FileInfo $target).Length -ne $mine) { $state = 'Conflict' }
                        else { [System.IO.File]::Replace($partial, $target, [NullString]::Value) }
                    } finally {
                        if (Test-Path -LiteralPath $partial) { [System.IO.File]::Delete($partial) }
                    }
                }
                $results.Add([pscustomobject]@{ Id = $id; ProjectDir = $dir.Name; State = $state })
            }
        }
        Write-Utf8 {{ScriptRunner.Quote(outputPath)}} (ConvertTo-Json -InputObject $results.ToArray() -Compress)
        """;
}
