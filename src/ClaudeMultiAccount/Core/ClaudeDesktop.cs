using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

public enum InstallKind { None, Msix, Classic }

/// <param name="Location">MSIX install folder, or the classic launcher exe.</param>
public sealed record ClaudeInstall(InstallKind Kind, string? Location, string? Version, string? FamilyName);

/// <summary>Finds and launches Claude Desktop for the current user or for a profile user.</summary>
public static class ClaudeDesktop
{
    const string AppId = "Claude";

    public static ClaudeInstall Detect()
    {
        try
        {
            // Windows PowerShell 5.1: the Appx module isn't available in PowerShell 7 on every machine.
            ProcessRunner.Run(Paths.PowerShell, new[]
            {
                "-NoProfile", "-NonInteractive", "-Command",
                "Get-AppxPackage -Name Claude | Where-Object { $_.Publisher -like '*Anthropic*' } | " +
                "Select-Object -First 1 | ForEach-Object { $_.InstallLocation + '|' + $_.Version + '|' + $_.PackageFamilyName }",
            }, out var output, TimeSpan.FromSeconds(20));
            var parts = output.Trim().Split('|');
            if (parts.Length == 3 && parts.All(s => s.Length > 0))
                return new ClaudeInstall(InstallKind.Msix, parts[0], parts[1], parts[2]);
        }
        catch
        {
            // Fall through to the classic installer check.
        }

        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude", "claude.exe");
        if (File.Exists(classic))
            return new ClaudeInstall(InstallKind.Classic, classic, FileVersionInfo.GetVersionInfo(classic).ProductVersion, null);

        return new ClaudeInstall(InstallKind.None, null, null, null);
    }

    public static void LaunchCurrentUser(ClaudeInstall install)
    {
        switch (install.Kind)
        {
            case InstallKind.Msix:
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{install.FamilyName}!{AppId}") { UseShellExecute = true });
                break;
            case InstallKind.Classic:
                Process.Start(new ProcessStartInfo(install.Location!) { UseShellExecute = true });
                break;
            default:
                throw new LauncherException(S.ClaudeNotFound);
        }
    }

    /// <summary>
    /// Keeps the "Run as" process alive while the profile user's Claude runs. Windows gives that user access
    /// to this desktop only for the lifetime of the process CreateProcessWithLogonW started; when it exits at
    /// once (e.g. "cmd /c start"), Claude's GPU and network helper processes can't start and Claude quits
    /// without ever showing a window. cmd expands %LOCALAPPDATA% with the profile user's environment.
    /// </summary>
    const string LaunchScript = """
        @echo off
        set "ALIAS=%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe"
        set "CLASSIC=%LOCALAPPDATA%\AnthropicClaude\claude.exe"
        set "RUNNING="
        tasklist /nh /fi "username eq %USERDOMAIN%\%USERNAME%" /fi "imagename eq claude.exe" 2>nul | find /i "claude.exe" >nul && set "RUNNING=1"
        if exist "%ALIAS%" (start "" "%ALIAS%") else if exist "%CLASSIC%" (start "" "%CLASSIC%") else exit /b 9
        rem Claude was already open: the keeper started with it is still running, this click only brings it forward.
        if defined RUNNING exit /b 0
        :keep
        ping -n 16 127.0.0.1 >nul
        tasklist /nh /fi "username eq %USERDOMAIN%\%USERNAME%" /fi "imagename eq claude.exe" 2>nul | find /i "claude.exe" >nul && goto keep
        exit /b 0
        """;

    /// <summary><see cref="LaunchScript"/> with CRLF line endings, as cmd expects.</summary>
    internal static string LaunchScriptContent => LaunchScript.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";

    /// <summary>Starts the profile user's own Claude (MSIX alias or classic install inside the profile).</summary>
    public static void LaunchForProfile(Profile profile, string password)
    {
        var script = Path.Combine(Paths.RequireProfileWork(profile.WindowsUser), "launch-claude.cmd");
        // Never rewrite the file while an earlier keeper may be running it: cmd reads batch files as it goes.
        var content = LaunchScriptContent;
        if (!File.Exists(script) || File.ReadAllText(script) != content)
            File.WriteAllText(script, content, Encoding.ASCII);

        var code = RunAs.RunDetached(profile.WindowsUser, password, $"\"{Paths.Cmd}\" /d /c \"\"{script}\"\"", hidden: true, TimeSpan.FromSeconds(3));
        if (code == 9)
            throw new LauncherException(S.ErrClaudeMissingInProfile);
        if (code is not null and not 0)
            throw new LauncherException(string.Format(S.ErrStep, $"exit code {code}"));
    }

    /// <summary>Opens a PowerShell window running as the profile user (for the Claude Code CLI, git, gh...).</summary>
    public static void OpenTerminal(Profile profile, string password) =>
        RunAs.Run(profile.WindowsUser, password, BuildTerminalCommand(profile), hidden: false, wait: false);

    internal static string BuildTerminalCommand(Profile profile)
    {
        var folder = profile.SharedFolders.FirstOrDefault(Directory.Exists);
        var title = $"Claude - {profile.DisplayName}".Replace("\"", "");
        var script = $"$Host.UI.RawUI.WindowTitle = {ScriptRunner.Quote(title)}; " +
                     (folder is null ? "" : $"Set-Location -LiteralPath {ScriptRunner.Quote(folder)}; ") +
                     $"if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {{ Write-Host {ScriptRunner.Quote(S.TerminalHint)} -ForegroundColor DarkGray }}";
        return $"\"{Paths.PowerShell}\" -NoExit -NoLogo -Command \"{script}\"";
    }
}
