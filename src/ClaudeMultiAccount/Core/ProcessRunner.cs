using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

/// <summary>Runs a console tool as the current user, hidden, and collects its output.</summary>
public static class ProcessRunner
{
    /// <summary>Returns the exit code. Kills the process tree and throws when <paramref name="timeout"/> elapses.</summary>
    public static int Run(string exe, IEnumerable<string> args, out string output, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        // Read both streams asynchronously so a full pipe can't block the child, and the timeout really applies.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout is null ? Timeout.Infinite : (int)timeout.Value.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new LauncherException(S.ErrTimeout);
        }
        p.WaitForExit(); // flushes the async readers
        output = stdout.Result + stderr.Result;
        return p.ExitCode;
    }
}
