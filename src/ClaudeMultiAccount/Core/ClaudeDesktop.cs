using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// Starts the profile user's own Claude. cmd expands %LOCALAPPDATA% with that user's environment,
    /// so this finds the MSIX alias or the classic install inside the profile.
    /// </summary>
    public static void LaunchForProfile(Profile profile, string password)
    {
        const string msixAlias = @"%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe";
        const string classicExe = @"%LOCALAPPDATA%\AnthropicClaude\claude.exe";
        var command =
            $"\"{Paths.Cmd}\" /d /c if exist \"{msixAlias}\" (start \"\" \"{msixAlias}\") " +
            $"else if exist \"{classicExe}\" (start \"\" \"{classicExe}\") else exit /b 9";

        var code = RunAs.Run(profile.WindowsUser, password, command, hidden: true, wait: true, TimeSpan.FromMinutes(2));
        if (code == 9)
            throw new LauncherException(S.ErrClaudeMissingInProfile);
        if (code != 0)
            throw new LauncherException(string.Format(S.ErrStep, $"exit code {code}"));
    }

    /// <summary>Opens a PowerShell window running as the profile user (for the Claude Code CLI, git, gh...).</summary>
    public static void OpenTerminal(Profile profile, string password)
    {
        var folder = profile.SharedFolders.FirstOrDefault(Directory.Exists);
        var title = $"Claude - {profile.DisplayName}".Replace("\"", "");
        var script = $"$Host.UI.RawUI.WindowTitle = {ScriptRunner.Quote(title)}; " +
                     (folder is null ? "" : $"Set-Location -LiteralPath {ScriptRunner.Quote(folder)}; ") +
                     $"if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {{ Write-Host {ScriptRunner.Quote(S.TerminalHint)} -ForegroundColor DarkGray }}";
        var command = $"\"{Paths.PowerShell}\" -NoExit -NoLogo -Command \"{script}\"";
        RunAs.Run(profile.WindowsUser, password, command, hidden: false, wait: false);
    }
}
