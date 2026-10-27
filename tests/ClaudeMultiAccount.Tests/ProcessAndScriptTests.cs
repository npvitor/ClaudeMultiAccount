using System;
using System.Diagnostics;
using System.IO;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public void Returns_exit_code_and_stdout()
    {
        var code = ProcessRunner.Run(Paths.Cmd, new[] { "/d", "/c", "echo hello& exit 3" }, out var output);
        Assert.Equal(3, code);
        Assert.Contains("hello", output);
    }

    [Fact]
    public void Captures_stderr_too()
    {
        ProcessRunner.Run(Paths.Cmd, new[] { "/d", "/c", "echo oops 1>&2" }, out var output);
        Assert.Contains("oops", output);
    }

    [Fact]
    public void Arguments_with_spaces_and_quotes_arrive_intact()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("echo.ps1"), "Write-Output ('[' + $args[0] + ']')");
        ProcessRunner.Run(Paths.PowerShell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", dir.File("echo.ps1"), "a b \"c\" 'd'" }, out var output);
        Assert.Contains("[a b \"c\" 'd']", output);
    }

    [Fact]
    public void Large_output_does_not_deadlock()
    {
        var code = ProcessRunner.Run(Paths.PowerShell, new[] { "-NoProfile", "-Command", "1..20000 | % { 'line ' + $_ }; [Console]::Error.WriteLine('x' * 100000)" },
            out var output, TimeSpan.FromMinutes(1));
        Assert.Equal(0, code);
        Assert.Contains("line 20000", output);
    }

    [Fact]
    public void Timeout_kills_the_process_and_throws()
    {
        var watch = Stopwatch.StartNew();
        var ex = Assert.Throws<LauncherException>(() =>
            ProcessRunner.Run(Paths.Cmd, new[] { "/d", "/c", "ping -n 30 127.0.0.1 >nul" }, out _, TimeSpan.FromSeconds(1)));
        Assert.Equal(S.ErrTimeout, ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
    }
}

public class ScriptRunnerTests
{
    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("''", "''''''")]
    [InlineData("", "''")]
    [InlineData("$env:x `n \"q\"", "'$env:x `n \"q\"'")]
    [InlineData("O’Brien", "'O’’Brien'")]
    [InlineData("‘a’‚b‛", "'‘‘a’’‚‚b‛‛'")]
    public void Quote_makes_a_powershell_literal(string value, string expected) =>
        Assert.Equal(expected, ScriptRunner.Quote(value));

    [Theory]
    [InlineData("it's")]
    [InlineData("O’Brien ‘x’ ‚y‛")]
    [InlineData("x’; Remove-Item C:\\nope; ’")]
    [InlineData("$(Remove-Item C:\\nope) `whoami` ; exit 5")]
    [InlineData("João ✓ \"aspas\"")]
    public void Quoted_values_reach_powershell_unchanged(string value)
    {
        using var dir = new TempDir();
        var outFile = dir.File("out.txt");
        var script = ScriptRunner.BuildScript(
            $"[IO.File]::WriteAllText({ScriptRunner.Quote(outFile)}, {ScriptRunner.Quote(value)})", dir.File("x.log"));
        var (code, output) = TestEnv.RunPowerShellScript(script, dir.File("s.ps1"));
        Assert.True(code == 0, output);
        Assert.Equal(value, File.ReadAllText(outFile));
    }

    [Fact]
    public void Wrapped_script_exits_0_and_writes_no_log_on_success()
    {
        using var dir = new TempDir();
        var log = dir.File("step.log");
        var (code, output) = TestEnv.RunPowerShellScript(ScriptRunner.BuildScript("$x = 1 + 1", log), dir.File("s.ps1"));
        Assert.True(code == 0, output);
        Assert.False(File.Exists(log));
    }

    [Theory]
    [InlineData("throw 'boom-123'", "boom-123")]
    [InlineData("Get-Item -LiteralPath 'C:\\definitely\\missing\\boom-456'", "boom-456")] // non-terminating error becomes fatal
    public void Wrapped_script_exits_1_and_logs_the_error(string body, string expected)
    {
        using var dir = new TempDir();
        var log = dir.File("step.log");
        var (code, output) = TestEnv.RunPowerShellScript(ScriptRunner.BuildScript(body, log), dir.File("s.ps1"));
        Assert.True(code == 1, output);
        Assert.Contains(expected, File.ReadAllText(log));
    }

    [Fact]
    public void Wrapped_script_handles_non_ascii_text()
    {
        using var dir = new TempDir();
        var outFile = dir.File("out.txt");
        var script = ScriptRunner.BuildScript($"[IO.File]::WriteAllText({ScriptRunner.Quote(outFile)}, 'João Ação')", dir.File("x.log"));
        var (code, output) = TestEnv.RunPowerShellScript(script, dir.File("s.ps1"));
        Assert.True(code == 0, output);
        Assert.Equal("João Ação", File.ReadAllText(outFile));
    }

    [Fact]
    public void Run_without_work_folder_asks_for_repair()
    {
        var profile = new Profile { WindowsUser = "claude-nowork-" + Guid.NewGuid().ToString("N")[..5] };
        var ex = Assert.Throws<LauncherException>(() => ScriptRunner.Run(profile, "pw", "x", "exit 0", TimeSpan.FromSeconds(5)));
        Assert.Equal(S.ErrWorkFolderMissing, ex.Message);
    }
}

public class RunAsTests
{
    [Fact]
    public void Command_line_over_1024_characters_is_rejected_before_calling_windows()
    {
        Assert.Throws<ArgumentException>(() => RunAs.Run("claude-x", "pw", new string('a', 1025), hidden: true, wait: true));
        Assert.Throws<ArgumentException>(() => RunAs.RunDetached("claude-x", "pw", new string('a', 1025), hidden: true, TimeSpan.Zero));
    }

    [Fact]
    public void Unknown_user_or_wrong_password_gives_friendly_error()
    {
        var user = "claude-nouser-" + Guid.NewGuid().ToString("N")[..5];
        var ex = Assert.Throws<LauncherException>(() =>
            RunAs.Run(user, "wrong", $"\"{Paths.Cmd}\" /c exit 0", hidden: true, wait: true, TimeSpan.FromSeconds(10)));
        // 1326 normally; a disabled Secondary Logon service gives its own message, also friendly.
        Assert.Contains(ex.Message, new[] { S.ErrBadPassword, S.ErrSecLogon });
    }
}

public class PathsTests
{
    [Fact]
    public void Temp_is_created_on_demand()
    {
        Assert.True(Directory.Exists(Paths.Temp));
        Assert.StartsWith(Path.GetTempPath(), Paths.Temp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locations_are_where_the_readme_says()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeMultiAccount", "profiles.json"), Paths.ProfilesFile);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClaudeMultiAccount", "claude-x"), Paths.ProfileWork("claude-x"));
        Assert.True(File.Exists(Paths.Cmd));
        Assert.True(File.Exists(Paths.PowerShell));
    }

    [Fact]
    public void RequireProfileWork_throws_when_missing()
    {
        var ex = Assert.Throws<LauncherException>(() => Paths.RequireProfileWork("claude-nowork-" + Guid.NewGuid().ToString("N")[..5]));
        Assert.Equal(S.ErrWorkFolderMissing, ex.Message);
    }

    [Fact]
    public void TryDelete_ignores_missing_and_deletes_existing()
    {
        using var dir = new TempDir();
        Paths.TryDelete(dir.File("missing"));
        File.WriteAllText(dir.File("x"), "x");
        Paths.TryDelete(dir.File("x"));
        Assert.False(File.Exists(dir.File("x")));
    }
}
