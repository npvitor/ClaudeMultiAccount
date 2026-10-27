using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using ClaudeMultiAccount.Localization;
using Microsoft.Win32;

namespace ClaudeMultiAccount.Core;

/// <summary>Local Windows account helpers. Everything that changes the system requires elevation.</summary>
public static class LocalUsers
{
    const string HiddenUsersKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public static bool Exists(string userName) => TryGetSid(userName) is not null;

    public static string? TryGetSid(string userName)
    {
        try
        {
            var account = new NTAccount(Environment.MachineName, userName);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>Turns "Pessoal" into a valid, unused Windows user name such as "claude-pessoal".</summary>
    public static string SuggestUserName(string displayName, IEnumerable<Profile> existing)
    {
        var normalized = displayName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            var lower = char.ToLowerInvariant(c);
            sb.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' ? lower : '-');
        }
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0)
            slug = "profile";

        // Windows user names are limited to 20 characters.
        var baseName = ("claude-" + slug);
        if (baseName.Length > 20)
            baseName = baseName[..20].TrimEnd('-');

        var taken = new HashSet<string>(existing.Select(p => p.WindowsUser), StringComparer.OrdinalIgnoreCase);
        var candidate = baseName;
        for (int n = 2; taken.Contains(candidate) || Exists(candidate); n++)
        {
            var suffix = n.ToString(CultureInfo.InvariantCulture);
            candidate = baseName[..Math.Min(baseName.Length, 20 - suffix.Length)].TrimEnd('-') + suffix;
        }
        return candidate;
    }

    public static void Create(string userName, string password, string fullName)
    {
        var info = new Native.USER_INFO_1
        {
            usri1_name = userName,
            usri1_password = password,
            usri1_priv = Native.USER_PRIV_USER,
            usri1_comment = S.UserComment,
            usri1_flags = Native.UF_SCRIPT | Native.UF_DONT_EXPIRE_PASSWD | Native.UF_PASSWD_CANT_CHANGE,
        };
        int rc = Native.NetUserAdd(null, 1, ref info, out _);
        if (rc == Native.NERR_UserExists)
            throw new LauncherException(string.Format(S.ErrUserExists, userName));
        if (rc == Native.NERR_PasswordTooShort)
            throw new LauncherException(S.ErrPasswordPolicy);
        if (rc != 0)
            throw new LauncherException(string.Format(S.ErrCreateUser, Describe(rc)));

        var full = new Native.USER_INFO_1011 { usri1011_full_name = fullName };
        Native.NetUserSetInfo(null, userName, 1011, ref full, out _);
    }

    /// <summary>Adds the user to, or removes it from, a built-in group such as Users or Administrators.</summary>
    public static void SetGroupMembership(string userName, WellKnownSidType group, bool member)
    {
        // Built-in groups have localized names ("Usuários", "Administradores"...), so resolve them by SID.
        var account = new SecurityIdentifier(group, null).Translate(typeof(NTAccount)).Value;
        var groupName = account[(account.IndexOf('\\') + 1)..];
        var info = new Native.LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = Environment.MachineName + "\\" + userName };
        int rc = member
            ? Native.NetLocalGroupAddMembers(null, groupName, 3, ref info, 1)
            : Native.NetLocalGroupDelMembers(null, groupName, 3, ref info, 1);
        if (rc != 0 && rc != Native.ERROR_MEMBER_IN_ALIAS && rc != Native.ERROR_MEMBER_NOT_IN_ALIAS)
            throw new LauncherException(string.Format(S.ErrGroup, groupName, Describe(rc)));
    }

    public static void Delete(string userName)
    {
        int rc = Native.NetUserDel(null, userName);
        if (rc != 0 && rc != Native.NERR_UserNotFound)
            throw new LauncherException(string.Format(S.ErrDeleteUser, Describe(rc)));
    }

    /// <summary>Hides (or shows again) the account on the sign-in screen.</summary>
    public static void SetHidden(string userName, bool hidden)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(HiddenUsersKey, writable: true);
        if (hidden)
            key.SetValue(userName, 0, RegistryValueKind.DWord);
        else
            key.DeleteValue(userName, throwOnMissingValue: false);
    }

    /// <summary>Closes every process running as the user (e.g. its Claude window) before removal.</summary>
    public static void KillProcesses(string userName)
    {
        RunTool("taskkill.exe", out _, "/F", "/T", "/FI", $"USERNAME eq {userName}", "/IM", "*");
    }

    /// <summary>
    /// Deletes the user's profile folder (C:\Users\&lt;user&gt;) and its registry entry.
    /// Returns the folder path when Windows couldn't delete it, so the caller can tell the user.
    /// </summary>
    public static string? DeleteWindowsProfile(string sid)
    {
        string? path;
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = hklm.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}"))
            path = key?.GetValue("ProfileImagePath") as string;
        if (path is null)
            return null; // the profile was never created
        path = Environment.ExpandEnvironmentVariables(path);

        // The profile hive can stay loaded for a moment after the user's processes exit.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (Native.DeleteProfile(sid, null, null))
                return null;
            Thread.Sleep(1500);
        }
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Gives the user modify rights on a folder (inherited by everything inside it). Returns false when the
    /// folder already allows it, which is common: folders created at the drive root grant Modify to
    /// Authenticated Users. Skipping matters because icacls rewrites the ACL of every file below the
    /// folder, which takes many minutes on a big repos folder.
    /// </summary>
    public static bool GrantFolder(string folder, string sid)
    {
        if (!Directory.Exists(folder))
            throw new LauncherException(string.Format(S.ErrFolderMissing, folder));
        if (HasModifyAccess(folder, sid))
            return false;
        if (RunTool("icacls.exe", out var output, folder, "/grant", $"*{sid}:(OI)(CI)M", "/C", "/Q") != 0)
            throw new LauncherException(string.Format(S.ErrAcl, folder, output.Trim()));
        return true;
    }

    /// <summary>Removes the entry <see cref="GrantFolder"/> added. Folders it skipped have none, so they're left alone.</summary>
    public static void RevokeFolder(string folder, string sid)
    {
        if (!Directory.Exists(folder))
            return;
        var explicitRules = new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        if (explicitRules.Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Value == sid))
            RunTool("icacls.exe", out _, folder, "/remove:g", $"*{sid}", "/C", "/Q");
    }

    /// <summary>
    /// True when the folder's ACL already lets the user modify the folder and everything inside it,
    /// directly or through Everyone, Authenticated Users or Users.
    /// </summary>
    public static bool HasModifyAccess(string folder, string sid)
    {
        var who = new HashSet<string> { "S-1-1-0", "S-1-5-11", "S-1-5-32-545", sid };
        var rules = new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        bool self = false, children = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (!who.Contains(rule.IdentityReference.Value))
                continue;
            if (rule.AccessControlType == AccessControlType.Deny)
            {
                if ((rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete)) != 0)
                    return false;
                continue;
            }
            if ((rule.FileSystemRights & FileSystemRights.Modify) != FileSystemRights.Modify)
                continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                self = true;
            if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit) &&
                rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit) &&
                (rule.PropagationFlags & PropagationFlags.NoPropagateInherit) == 0)
                children = true;
        }
        return self && children;
    }

    static int RunTool(string exe, out string output, params string[] args) =>
        ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, exe), args, out output);

    static string Describe(int rc)
    {
        var message = new Win32Exception(rc).Message;
        return $"{message} ({rc})";
    }
}
