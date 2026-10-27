using System;
using System.IO;
using System.Text;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

/// <summary>A script ran inside the profile and reported failure (as opposed to not being able to start).</summary>
public sealed class ScriptFailedException : LauncherException
{
    public ScriptFailedException(string message) : base(message) { }
}

/// <summary>
/// Runs a PowerShell snippet inside a profile. The snippet goes to a .ps1 file in the profile's locked-down
/// work folder because CreateProcessWithLogonW only accepts 1024 characters of command line. Errors land
/// in a log file next to it.
/// </summary>
public static class ScriptRunner
{
    public static void Run(Profile profile, string password, string step, string body, TimeSpan timeout)
    {
        var work = Paths.RequireProfileWork(profile.WindowsUser);
        var id = $"{step}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var scriptPath = Path.Combine(work, id + ".ps1");
        var logPath = Path.Combine(work, id + ".log");

        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine("try {")
            .AppendLine(body)
            .AppendLine("} catch {")
            .AppendLine($"  ($_ | Out-String) | Out-File -LiteralPath {Quote(logPath)} -Encoding utf8")
            .AppendLine("  exit 1")
            .AppendLine("}")
            .AppendLine("exit 0")
            .ToString();
        // BOM so Windows PowerShell 5.1 reads non-ASCII names (e.g. "João") correctly.
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            var command = $"\"{Paths.PowerShell}\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
            var code = RunAs.Run(profile.WindowsUser, password, command, hidden: true, wait: true, timeout);
            if (code != 0)
            {
                var detail = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : $"exit code {code}";
                throw new ScriptFailedException(string.Format(S.ErrStep, detail));
            }
            Paths.TryDelete(logPath);
        }
        finally
        {
            Paths.TryDelete(scriptPath);
        }
    }

    /// <summary>PowerShell single-quoted literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
