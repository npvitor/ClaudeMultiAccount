using System;
using System.IO;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

public static class Paths
{
    /// <summary>Per-user data (profiles and encrypted passwords). Only the launcher's user can read it.</summary>
    public static string UserData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeMultiAccount");

    public static string ProfilesFile => Path.Combine(UserData, "profiles.json");

    /// <summary>Machine-wide root (ProgramData). Locked down by the elevated step so only admins can add entries.</summary>
    public static string Shared =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClaudeMultiAccount");

    /// <summary>
    /// Per-profile folder for the short-lived setup scripts, their logs and copied installers. The elevated
    /// create/secure step makes it readable only by admins, the launcher's user and that profile's user,
    /// so no other account on the machine can plant or swap a file that will run inside the profile.
    /// </summary>
    public static string ProfileWork(string windowsUser) => Path.Combine(Shared, windowsUser);

    /// <summary><see cref="ProfileWork"/>, or a friendly error when it is missing (profile needs a repair).</summary>
    public static string RequireProfileWork(string windowsUser)
    {
        var path = ProfileWork(windowsUser);
        if (!Directory.Exists(path))
            throw new LauncherException(S.ErrWorkFolderMissing);
        return path;
    }

    public static string Temp => Ensure(Path.Combine(Path.GetTempPath(), "ClaudeMultiAccount"));

    public static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
    public static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
