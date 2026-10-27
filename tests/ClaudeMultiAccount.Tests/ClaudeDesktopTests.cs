using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class ClaudeDesktopTests
{
    [Fact]
    public void Detect_returns_a_consistent_result_for_this_machine()
    {
        var install = ClaudeDesktop.Detect();
        switch (install.Kind)
        {
            case InstallKind.Msix:
                Assert.True(Directory.Exists(install.Location), install.Location);
                // RegisterClaude registers this manifest for the profile user.
                Assert.True(File.Exists(Path.Combine(install.Location!, "AppxManifest.xml")));
                Assert.False(string.IsNullOrEmpty(install.Version));
                // The elevated Provision step refuses names that don't match; a real one must pass.
                Assert.True(Elevation.IsPackageFamilyName(install.FamilyName!), install.FamilyName);
                break;
            case InstallKind.Classic:
                Assert.True(File.Exists(install.Location), install.Location);
                Assert.Null(install.FamilyName);
                break;
            default:
                Assert.Null(install.Location);
                break;
        }
    }

    [Fact]
    public void LaunchCurrentUser_without_claude_throws_friendly_error()
    {
        var ex = Assert.Throws<LauncherException>(() => ClaudeDesktop.LaunchCurrentUser(new ClaudeInstall(InstallKind.None, null, null, null)));
        Assert.Equal(S.ClaudeNotFound, ex.Message);
    }

    [Fact]
    public void LaunchForProfile_without_work_folder_asks_for_repair()
    {
        var profile = new Profile { WindowsUser = "claude-nowork-" + Guid.NewGuid().ToString("N")[..5] };
        var ex = Assert.Throws<LauncherException>(() => ClaudeDesktop.LaunchForProfile(profile, "pw"));
        Assert.Equal(S.ErrWorkFolderMissing, ex.Message);
    }

    [Fact]
    public void Launch_script_is_ascii_with_crlf_line_endings()
    {
        var content = ClaudeDesktop.LaunchScriptContent;
        // It is written with Encoding.ASCII: anything else would be mangled into '?'.
        Assert.All(content, c => Assert.True(c < 128, $"non-ASCII char U+{(int)c:X4}"));
        Assert.DoesNotContain("\n", content.Replace("\r\n", ""));
        Assert.DoesNotContain("\r\r", content);
        Assert.EndsWith("\r\n", content);
        Assert.StartsWith("@echo off\r\n", content);
    }

    [Fact]
    public void Launch_script_is_stable_so_a_running_keeper_is_never_rewritten()
    {
        Assert.Equal(ClaudeDesktop.LaunchScriptContent, ClaudeDesktop.LaunchScriptContent);
    }

    [Fact]
    public void Launch_script_exits_9_when_claude_is_missing_in_the_profile()
    {
        using var dir = new TempDir();
        var script = dir.File("launch-claude.cmd");
        File.WriteAllText(script, ClaudeDesktop.LaunchScriptContent, Encoding.ASCII);
        var fakeLocalAppData = Directory.CreateDirectory(dir.File("LocalAppData")).FullName;

        var (code, output) = TestEnv.Run(Paths.Cmd, new[] { "/d", "/c", script },
            new Dictionary<string, string> { ["LOCALAPPDATA"] = fakeLocalAppData });

        // LaunchForProfile maps exit code 9 to "Claude isn't installed in this profile".
        Assert.True(code == 9, $"exit {code}: {output}");
    }

    [Fact]
    public void Terminal_command_fits_createprocesswithlogonw_and_quotes_safely()
    {
        using var dir = new TempDir(" it's a \"repo\"".Replace("\"", "")); // folders can't contain quotes
        var profile = new Profile
        {
            DisplayName = "Cliente \"X\" d'Ávila",
            SharedFolders = { @"C:\definitely\missing", dir.Path },
        };

        var command = ClaudeDesktop.BuildTerminalCommand(profile);

        Assert.True(command.Length <= 1024, command.Length.ToString());
        Assert.StartsWith($"\"{Paths.PowerShell}\" -NoExit -NoLogo -Command \"", command);
        Assert.EndsWith("\"", command);
        // Only the outer quotes of the exe and of -Command: inner double quotes would split the arguments.
        Assert.Equal(4, command.Count(c => c == '"'));
        Assert.Contains(ScriptRunner.Quote(dir.Path), command); // first existing folder
        Assert.DoesNotContain("missing", command);
        Assert.Contains("'Claude - Cliente X d''Ávila'", command);
    }

    [Fact]
    public void Terminal_command_without_existing_folder_has_no_set_location()
    {
        var command = ClaudeDesktop.BuildTerminalCommand(new Profile { DisplayName = "x", SharedFolders = { @"Z:\nope" } });
        Assert.DoesNotContain("Set-Location", command);
    }

    [Fact]
    public void Terminal_command_parses_as_valid_powershell()
    {
        using var dir = new TempDir(" it's");
        var command = ClaudeDesktop.BuildTerminalCommand(new Profile { DisplayName = "Teste d'Ávila", SharedFolders = { dir.Path } });
        var inner = command[(command.IndexOf("-Command \"", StringComparison.Ordinal) + 10)..^1];
        var check = "$e = $null; [void][Management.Automation.Language.Parser]::ParseInput($env:CMA_SCRIPT, [ref]$null, [ref]$e); if ($e.Count) { $e | % Message; exit 1 }";
        var (code, output) = TestEnv.Run(Paths.PowerShell, new[] { "-NoProfile", "-Command", check },
            new Dictionary<string, string> { ["CMA_SCRIPT"] = inner });
        Assert.True(code == 0, output);
    }
}

public class ProfileSetupTests
{
    static (int Code, string Output) RunGitScript(Profile profile, string globalConfig, TempDir dir, string? path = null)
    {
        var env = new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = globalConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" };
        if (path is not null)
            env["PATH"] = path;
        var script = ScriptRunner.BuildScript(ProfileSetup.BuildGitScript(profile), dir.File("git.log"));
        return TestEnv.RunPowerShellScript(script, dir.File("git-" + Guid.NewGuid().ToString("N")[..4] + ".ps1"), env);
    }

    static string[] GitConfig(string file, string key)
    {
        var (_, output) = TestEnv.Run("git", new[] { "config", "--file", file, "--get-all", key });
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public void Git_script_adds_safe_directories_and_identity_once()
    {
        Assert.SkipWhen(TestEnv.FindOnPath("git.exe") is null, "Git is not installed");
        using var dir = new TempDir();
        var config = dir.File(".gitconfig");
        var quoted = Directory.CreateDirectory(dir.File("it's here")).FullName;
        var profile = new Profile
        {
            SharedFolders = { @"C:\Git\", quoted, @"D:\" },
            GitName = "Ana O'Neil",
            GitEmail = "ana@example.com",
        };

        for (int i = 0; i < 2; i++) // idempotent: the second run must not add duplicates
        {
            var (code, output) = RunGitScript(profile, config, dir);
            Assert.True(code == 0, output + (File.Exists(dir.File("git.log")) ? File.ReadAllText(dir.File("git.log")) : ""));
        }

        var q = quoted.Replace('\\', '/');
        Assert.Equal(new[] { "C:/Git", "C:/Git/*", q, q + "/*", "D:/", "D:/*" }, GitConfig(config, "safe.directory"));
        Assert.Equal(new[] { "Ana O'Neil" }, GitConfig(config, "user.name"));
        Assert.Equal(new[] { "ana@example.com" }, GitConfig(config, "user.email"));
    }

    [Fact]
    public void Git_script_keeps_existing_entries_and_skips_empty_identity()
    {
        Assert.SkipWhen(TestEnv.FindOnPath("git.exe") is null, "Git is not installed");
        using var dir = new TempDir();
        var config = dir.File(".gitconfig");
        File.WriteAllText(config, "[safe]\n\tdirectory = E:/other\n[user]\n\tname = Keep Me\n");

        var (code, output) = RunGitScript(new Profile { SharedFolders = { @"C:\Git" }, GitName = "  ", GitEmail = null }, config, dir);

        Assert.True(code == 0, output);
        Assert.Equal(new[] { "E:/other", "C:/Git", "C:/Git/*" }, GitConfig(config, "safe.directory"));
        Assert.Equal(new[] { "Keep Me" }, GitConfig(config, "user.name"));
    }

    [Fact]
    public void Git_script_is_a_no_op_when_git_is_missing()
    {
        using var dir = new TempDir();
        var config = dir.File(".gitconfig");
        var (code, output) = RunGitScript(new Profile { SharedFolders = { @"C:\Git" }, GitName = "x" }, config, dir,
            path: Environment.SystemDirectory + ";" + Path.GetDirectoryName(Paths.PowerShell));
        Assert.True(code == 0, output);
        Assert.False(File.Exists(config));
    }

    [Fact]
    public async Task EnsureUpToDate_does_nothing_for_classic_or_same_version()
    {
        var profile = new Profile { WindowsUser = "claude-nowork-x", RegisteredClaudeVersion = "1.0" };
        var progress = new ListProgress();
        using var dir = new TempDir();
        var store = ProfileStore.Load(dir.File("p.json"));

        await ProfileSetup.EnsureUpToDateAsync(profile, "pw", new ClaudeInstall(InstallKind.Classic, "x", "2.0", null), store, progress);
        await ProfileSetup.EnsureUpToDateAsync(profile, "pw", new ClaudeInstall(InstallKind.Msix, "x", "1.0", "Claude_abcdefghijklm"), store, progress);
        await ProfileSetup.EnsureUpToDateAsync(profile, "pw", new ClaudeInstall(InstallKind.None, null, null, null), store, progress);

        Assert.Empty(progress.Reports);
        Assert.False(File.Exists(dir.File("p.json")));
    }

    [Fact]
    public async Task EnsureUpToDate_re_registers_when_main_account_updated()
    {
        var profile = new Profile { DisplayName = "Pessoal", WindowsUser = "claude-nowork-" + Guid.NewGuid().ToString("N")[..5], RegisteredClaudeVersion = "1.0" };
        var progress = new ListProgress();
        using var dir = new TempDir();

        // No work folder for this fake user, so the registration stops at its first step with a friendly error.
        var ex = await Assert.ThrowsAsync<LauncherException>(() =>
            ProfileSetup.EnsureUpToDateAsync(profile, "pw", new ClaudeInstall(InstallKind.Msix, @"C:\x", "2.0", "Claude_abcdefghijklm"), ProfileStore.Load(dir.File("p.json")), progress));

        Assert.Equal(S.ErrWorkFolderMissing, ex.Message);
        Assert.Equal(new[] { string.Format(S.StatusUpdatingProfile, "Pessoal") }, progress.Reports);
        Assert.Equal("1.0", profile.RegisteredClaudeVersion);
    }

    [Fact]
    public void RegisterClaude_classic_without_installer_is_refused()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<LauncherException>(() => ProfileSetup.RegisterClaude(new Profile { WindowsUser = "claude-x" }, "pw",
            new ClaudeInstall(InstallKind.Classic, "x", "1", null), null, ProfileStore.Load(dir.File("p.json"))));
        Assert.Equal(S.ErrNeedInstaller, ex.Message);
    }
}
