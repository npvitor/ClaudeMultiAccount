using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeMultiAccount.Core;

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    /// <summary>The hidden local Windows account that runs this profile's Claude.</summary>
    public string WindowsUser { get; set; } = "";
    public string Color { get; set; } = "#4F46E5";
    /// <summary>DPAPI-protected password of <see cref="WindowsUser"/>.</summary>
    public string ProtectedPassword { get; set; } = "";
    public List<string> SharedFolders { get; set; } = new();
    public string? GitName { get; set; }
    public string? GitEmail { get; set; }
    /// <summary>
    /// The hidden user is in the Administrators group. Its programs still run without admin rights
    /// (UAC filtered token); commands that need them get a Yes/No prompt instead of a password prompt.
    /// </summary>
    public bool IsAdmin { get; set; }
    /// <summary>Claude package version last registered for this profile (MSIX installs).</summary>
    public string? RegisteredClaudeVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// The saved profiles. <see cref="Save"/> also runs on background threads (setup steps), so the list is
/// only changed through <see cref="Add"/> and <see cref="Remove"/>, which share Save's lock.
/// </summary>
public sealed class ProfileStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _gate = new();
    readonly List<Profile> _profiles = new();

    public IReadOnlyList<Profile> Profiles => _profiles;

    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        try
        {
            if (File.Exists(Paths.ProfilesFile))
                store._profiles.AddRange(JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(Paths.ProfilesFile)) ?? new());
        }
        catch (Exception)
        {
            // Keep a copy of the unreadable file instead of silently losing the profiles.
            try { File.Copy(Paths.ProfilesFile, Paths.ProfilesFile + ".corrupt", overwrite: true); } catch { }
        }
        return store;
    }

    public void Add(Profile profile)
    {
        lock (_gate)
            _profiles.Add(profile);
    }

    public void Remove(Profile profile)
    {
        lock (_gate)
            _profiles.Remove(profile);
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Paths.UserData);
            var tmp = Paths.ProfilesFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles, Json));
            File.Move(tmp, Paths.ProfilesFile, overwrite: true);
        }
    }
}

/// <summary>Error with a message that can be shown to the user as is.</summary>
public class LauncherException : Exception
{
    public LauncherException(string message) : base(message) { }
}
