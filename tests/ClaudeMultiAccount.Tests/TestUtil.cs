using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace ClaudeMultiAccount.Tests;

/// <summary>A fresh folder under %TEMP% that is removed when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string? suffix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cma-tests", Guid.NewGuid().ToString("N")[..8] + suffix);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

/// <summary>Switches the UI culture for the current test and restores it afterwards.</summary>
public sealed class CultureScope : IDisposable
{
    readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public CultureScope(string name) => CultureInfo.CurrentUICulture = new CultureInfo(name);

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}

public static class TestEnv
{
    public static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;

    public static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Runs a tool with extra environment variables; returns exit code and combined output.</summary>
    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, IDictionary<string, string>? env = null, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} did not finish in {timeoutMs} ms");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Runs a PowerShell script file the way <c>ScriptRunner</c> does (UTF-8 with BOM, -File).</summary>
    public static (int Code, string Output) RunPowerShellScript(string scriptText, string scriptPath, IDictionary<string, string>? env = null)
    {
        File.WriteAllText(scriptPath, scriptText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return Run(Core.Paths.PowerShell, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }, env);
    }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> (the BCL one posts to a sync context).</summary>
public sealed class ListProgress : IProgress<string>
{
    public List<string> Reports { get; } = new();
    public void Report(string value) => Reports.Add(value);
}
