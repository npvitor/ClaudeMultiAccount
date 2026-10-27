using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

/// <summary>Starts processes as a profile's hidden Windows user (same API as "runas").</summary>
public static class RunAs
{
    /// <summary>
    /// Runs <paramref name="commandLine"/> as <paramref name="user"/> with that user's own profile and environment.
    /// Returns the exit code when <paramref name="wait"/> is true, otherwise 0.
    /// </summary>
    public static uint Run(string user, string password, string commandLine, bool hidden, bool wait, TimeSpan? timeout = null)
    {
        var pi = Start(user, password, commandLine, hidden);
        try
        {
            if (!wait)
                return 0;
            uint ms = timeout is null ? Native.INFINITE : (uint)timeout.Value.TotalMilliseconds;
            if (Native.WaitForSingleObject(pi.hProcess, ms) == Native.WAIT_TIMEOUT)
            {
                // Don't leave the step running in the profile after the caller deletes its script and moves on.
                Native.TerminateProcess(pi.hProcess, 1);
                throw new LauncherException(S.ErrTimeout);
            }
            Native.GetExitCodeProcess(pi.hProcess, out var code);
            return code;
        }
        finally
        {
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
        }
    }

    /// <summary>
    /// Starts a long-running process and waits up to <paramref name="grace"/> for an early failure.
    /// Returns the exit code if it already ended, or null if it is still running (the normal case).
    /// </summary>
    public static uint? RunDetached(string user, string password, string commandLine, bool hidden, TimeSpan grace)
    {
        var pi = Start(user, password, commandLine, hidden);
        try
        {
            if (Native.WaitForSingleObject(pi.hProcess, (uint)grace.TotalMilliseconds) == Native.WAIT_TIMEOUT)
                return null;
            Native.GetExitCodeProcess(pi.hProcess, out var code);
            return code;
        }
        finally
        {
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
        }
    }

    static Native.PROCESS_INFORMATION Start(string user, string password, string commandLine, bool hidden)
    {
        // CreateProcessWithLogonW limits the command line to 1024 characters.
        if (commandLine.Length > 1024)
            throw new ArgumentException("Command line too long for CreateProcessWithLogonW.", nameof(commandLine));

        var si = new Native.STARTUPINFO
        {
            cb = Marshal.SizeOf<Native.STARTUPINFO>(),
            dwFlags = Native.STARTF_USESHOWWINDOW,
            wShowWindow = hidden ? Native.SW_HIDE : Native.SW_SHOWNORMAL,
        };
        uint flags = Native.CREATE_UNICODE_ENVIRONMENT | (hidden ? Native.CREATE_NO_WINDOW : Native.CREATE_NEW_CONSOLE);
        var cmd = new StringBuilder(commandLine, 1025);

        if (!Native.CreateProcessWithLogonW(user, ".", password, Native.LOGON_WITH_PROFILE, null, cmd, flags,
                IntPtr.Zero, Environment.SystemDirectory, ref si, out var pi))
            throw Friendly(Marshal.GetLastWin32Error());
        return pi;
    }

    static Exception Friendly(int error) => error switch
    {
        1326 => new LauncherException(S.ErrBadPassword),
        1058 or 1068 => new LauncherException(S.ErrSecLogon),
        1385 => new LauncherException(S.ErrLogonType),
        1331 or 1909 => new LauncherException(S.ErrAccountDisabled),
        _ => new LauncherException(string.Format(S.ErrRunAs, new Win32Exception(error).Message, error)),
    };
}
