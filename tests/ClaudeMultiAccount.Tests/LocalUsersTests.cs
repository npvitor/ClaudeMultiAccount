using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeMultiAccount.Core;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class SuggestUserNameTests
{
    static Profile P(string user) => new() { WindowsUser = user };

    [Theory]
    [InlineData("Pessoal", "claude-pessoal")]
    [InlineData("Trabalho X", "claude-trabalho-x")]
    [InlineData("João Ação", "claude-joao-acao")]
    [InlineData("  --Cliente__ABC--  ", "claude-cliente-abc")]
    [InlineData("v2", "claude-v2")]
    public void Builds_slug_from_display_name(string display, string expected)
    {
        // A real account with that name may already exist on the test machine; then a number is appended.
        var name = LocalUsers.SuggestUserName(display, Array.Empty<Profile>());
        Assert.StartsWith(expected, name);
        Assert.All(name[expected.Length..], c => Assert.True(char.IsAsciiDigit(c), name));
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("日本語")]
    [InlineData("")]
    public void Falls_back_to_profile_when_nothing_usable(string display)
    {
        var name = LocalUsers.SuggestUserName(display, Array.Empty<Profile>());
        Assert.StartsWith("claude-profile", name);
    }

    [Fact]
    public void Long_names_are_cut_to_20_without_trailing_dash()
    {
        var name = LocalUsers.SuggestUserName("abcdefghijkl-mnopqrstuvwxyz", Array.Empty<Profile>());
        Assert.True(name.Length <= 20, name);
        Assert.False(name.EndsWith('-'));
        Assert.StartsWith("claude-abcdefghijkl", name);
    }

    [Fact]
    public void Adds_numeric_suffix_for_names_used_by_other_profiles()
    {
        var existing = new[] { P("claude-zq-test"), P("CLAUDE-ZQ-TEST2") };
        Assert.Equal("claude-zq-test3", LocalUsers.SuggestUserName("zq test", existing));
    }

    [Fact]
    public void Suffix_still_fits_in_20_characters()
    {
        var baseName = LocalUsers.SuggestUserName("zqxwvutsrqponmlkjih", Array.Empty<Profile>());
        Assert.Equal(20, baseName.Length);
        var existing = Enumerable.Range(1, 12).Select(n => P(n == 1 ? baseName : baseName[..(20 - n.ToString().Length)] + n)).ToList();
        var next = LocalUsers.SuggestUserName("zqxwvutsrqponmlkjih", existing);
        Assert.True(next.Length <= 20, next);
        Assert.EndsWith("13", next);
        Assert.DoesNotContain(next, existing.Select(e => e.WindowsUser));
    }

    [Fact]
    public void Skips_names_of_existing_windows_accounts()
    {
        // The current account is real, so a profile named like it must get another name.
        var me = Environment.UserName;
        if (!Elevation.IsProfileUserName(me))
            return; // only meaningful when running inside a profile user (claude-*)
        var display = me["claude-".Length..];
        Assert.NotEqual(me, LocalUsers.SuggestUserName(display, Array.Empty<Profile>()), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Pessoal")]
    [InlineData("João da Silva Sauro Junior")]
    [InlineData("%%%")]
    public void Every_suggestion_is_accepted_by_the_elevated_validation(string display) =>
        Assert.True(Elevation.IsProfileUserName(LocalUsers.SuggestUserName(display, Array.Empty<Profile>())));
}

public class LocalUsersSidTests
{
    [Fact]
    public void TryGetSid_resolves_the_current_user()
    {
        Assert.Equal(TestEnv.CurrentSid, LocalUsers.TryGetSid(Environment.UserName));
        Assert.True(LocalUsers.Exists(Environment.UserName));
    }

    [Fact]
    public void TryGetSid_returns_null_for_unknown_user()
    {
        Assert.Null(LocalUsers.TryGetSid("claude-nouser-" + Guid.NewGuid().ToString("N")[..5]));
        Assert.False(LocalUsers.Exists("claude-nouser-" + Guid.NewGuid().ToString("N")[..5]));
    }
}

/// <summary>Folder sharing on folders the test owns (no admin needed), using the built-in Guests SID as the "profile user".</summary>
public class FolderAccessTests
{
    static readonly string Guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null).Value;

    [Fact]
    public void Current_user_already_has_modify_access_to_its_temp_folder()
    {
        using var dir = new TempDir();
        Assert.True(LocalUsers.HasModifyAccess(dir.Path, TestEnv.CurrentSid));
    }

    [Fact]
    public void Grant_then_revoke_round_trip()
    {
        using var dir = new TempDir();
        Assert.False(LocalUsers.HasModifyAccess(dir.Path, Guests));

        Assert.True(LocalUsers.GrantFolder(dir.Path, Guests));
        Assert.True(LocalUsers.HasModifyAccess(dir.Path, Guests));

        // Second grant is skipped: nothing to do.
        Assert.False(LocalUsers.GrantFolder(dir.Path, Guests));

        // The grant is inherited by content created later.
        var child = Directory.CreateDirectory(Path.Combine(dir.Path, "child")).FullName;
        Assert.True(LocalUsers.HasModifyAccess(child, Guests));

        LocalUsers.RevokeFolder(dir.Path, Guests);
        Assert.False(LocalUsers.HasModifyAccess(dir.Path, Guests));
        Assert.DoesNotContain(ExplicitRules(dir.Path), r => r.IdentityReference.Value == Guests);
    }

    [Fact]
    public void Grant_uses_inheritable_modify_rule()
    {
        using var dir = new TempDir();
        LocalUsers.GrantFolder(dir.Path, Guests);
        var rule = Assert.Single(ExplicitRules(dir.Path), r => r.IdentityReference.Value == Guests);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.Modify, rule.FileSystemRights & FileSystemRights.Modify);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
    }

    [Fact]
    public void Grant_works_on_paths_with_spaces_and_accents()
    {
        using var dir = new TempDir(" pasta ção");
        Assert.True(LocalUsers.GrantFolder(dir.Path, Guests));
        Assert.True(LocalUsers.HasModifyAccess(dir.Path, Guests));
    }

    [Fact]
    public void Deny_write_rule_wins_over_allow()
    {
        using var dir = new TempDir();
        AddRule(dir.Path, Guests, FileSystemRights.Modify, AccessControlType.Allow, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        Assert.True(LocalUsers.HasModifyAccess(dir.Path, Guests));
        AddRule(dir.Path, Guests, FileSystemRights.Write, AccessControlType.Deny, InheritanceFlags.None);
        Assert.False(LocalUsers.HasModifyAccess(dir.Path, Guests));
    }

    [Fact]
    public void Non_inherited_allow_is_not_enough()
    {
        using var dir = new TempDir();
        AddRule(dir.Path, Guests, FileSystemRights.Modify, AccessControlType.Allow, InheritanceFlags.None);
        Assert.False(LocalUsers.HasModifyAccess(dir.Path, Guests));
    }

    [Fact]
    public void Read_only_allow_is_not_enough()
    {
        using var dir = new TempDir();
        AddRule(dir.Path, Guests, FileSystemRights.ReadAndExecute, AccessControlType.Allow, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        Assert.False(LocalUsers.HasModifyAccess(dir.Path, Guests));
    }

    [Fact]
    public void Everyone_modify_counts_for_any_user()
    {
        using var dir = new TempDir();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
        AddRule(dir.Path, everyone, FileSystemRights.Modify, AccessControlType.Allow, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        Assert.True(LocalUsers.HasModifyAccess(dir.Path, Guests));
        Assert.False(LocalUsers.GrantFolder(dir.Path, Guests)); // skipped, no icacls run
    }

    [Fact]
    public void Grant_on_missing_folder_throws_friendly_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cma-missing-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<LauncherException>(() => LocalUsers.GrantFolder(missing, Guests));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Revoke_on_missing_folder_or_without_entry_is_a_no_op()
    {
        LocalUsers.RevokeFolder(Path.Combine(Path.GetTempPath(), "cma-missing-" + Guid.NewGuid().ToString("N")), Guests);
        using var dir = new TempDir();
        var before = ExplicitRules(dir.Path).Count;
        LocalUsers.RevokeFolder(dir.Path, Guests);
        Assert.Equal(before, ExplicitRules(dir.Path).Count);
    }

    static List<FileSystemAccessRule> ExplicitRules(string folder) =>
        new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

    static void AddRule(string folder, string sid, FileSystemRights rights, AccessControlType type, InheritanceFlags inheritance)
    {
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights, inheritance, PropagationFlags.None, type));
        info.SetAccessControl(acl);
    }
}
