using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using ClaudeMultiAccount.Core;
using Microsoft.Win32;
using Xunit;

namespace ClaudeMultiAccount.Tests;

/// <summary>
/// End-to-end lifecycle of a real hidden Windows user: create, prepare, run steps inside it, update, delete.
/// These change the machine, so they only run when explicitly asked for, from an elevated prompt:
///   set CMA_ADMIN_TESTS=1            (user, folders, RunAs, scripts, Git, admin flag, removal)
///   set CMA_ADMIN_TESTS_CLAUDE=1     (also registers Claude Desktop in the test user and launches it; slower)
/// Everything is removed at the end, even when an assertion fails.
/// </summary>
public class AdminIntegrationTests
{
    static void RequireAdminOptIn()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CMA_ADMIN_TESTS") == "1", "Set CMA_ADMIN_TESTS=1 to run tests that create a real Windows user.");
        Assert.SkipUnless(TestEnv.IsElevated, "Admin integration tests need an elevated (Run as administrator) prompt.");
    }

    static ElevatedResult Elevated(ElevatedRequest request)
    {
        request.RequesterSid = TestEnv.CurrentSid;
        using var dir = new TempDir();
        File.WriteAllText(dir.File("req.json"), JsonSerializer.Serialize(request));
        Elevation.RunElevated(dir.File("req.json"), dir.File("res.json"));
        return JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(dir.File("res.json")))!;
    }

    static bool IsHidden(string user)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList");
        return key?.GetValue(user) is 0;
    }

    static bool InGroup(string user, string groupSid)
    {
        ProcessRunner.Run(Paths.PowerShell, new[] { "-NoProfile", "-Command",
            $"Get-LocalGroupMember -SID {groupSid} | ForEach-Object {{ $_.Name }}" }, out var output);
        return output.Split('\n').Any(l => l.Trim().EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase));
    }

    static string? ProfileImagePath(string sid)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return key?.GetValue("ProfileImagePath") as string;
    }

    [Fact]
    public void Full_profile_lifecycle()
    {
        RequireAdminOptIn();

        var user = "claude-it-" + Guid.NewGuid().ToString("N")[..6];
        var password = Secret.GeneratePassword();
        using var shared = new TempDir(" shared");
        using var later = new TempDir(" later");
        using var data = new TempDir();
        var store = ProfileStore.Load(data.File("profiles.json"));
        var profile = new Profile
        {
            DisplayName = "IT " + user, WindowsUser = user, ProtectedPassword = Secret.Protect(password),
            SharedFolders = { shared.Path }, GitName = "Integration Test", GitEmail = "it@example.com",
        };
        string? sid = null;
        try
        {
            // Create
            var created = Elevated(new ElevatedRequest
            {
                Op = ElevatedOp.Create, UserName = user, FullName = "Claude - IT",
                ProtectedPassword = profile.ProtectedPassword, Folders = profile.SharedFolders, Admin = false,
            });
            Assert.True(created.Ok, created.Error);
            sid = LocalUsers.TryGetSid(user);
            Assert.NotNull(sid);
            Assert.True(IsHidden(user));
            Assert.True(InGroup(user, "S-1-5-32-545"));
            Assert.False(InGroup(user, "S-1-5-32-544"));
            Assert.True(LocalUsers.HasModifyAccess(shared.Path, sid!));

            // Work folder ACL: protected, only SYSTEM/Admins/requester/profile user.
            var work = Paths.ProfileWork(user);
            var rules = new DirectoryInfo(work).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
            Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544", TestEnv.CurrentSid, sid! }.ToHashSet(), rules);

            // Creating the same user again fails cleanly.
            var again = Elevated(new ElevatedRequest { Op = ElevatedOp.Create, UserName = user, ProtectedPassword = profile.ProtectedPassword });
            Assert.False(again.Ok);

            // First logon creates C:\Users\<user>
            ProfileSetup.PrepareWindowsProfile(profile, password);
            Assert.NotNull(ProfileImagePath(sid!));

            // RunAs exit codes, script runner success/failure, wrong password
            Assert.Equal(7u, RunAs.Run(user, password, $"\"{Paths.Cmd}\" /d /c exit 7", hidden: true, wait: true, TimeSpan.FromMinutes(1)));
            Assert.Equal(5u, RunAs.RunDetached(user, password, $"\"{Paths.Cmd}\" /d /c exit 5", hidden: true, TimeSpan.FromSeconds(30)));
            Assert.Null(RunAs.RunDetached(user, password, $"\"{Paths.Cmd}\" /d /c ping -n 4 127.0.0.1 >nul", hidden: true, TimeSpan.FromMilliseconds(200)));
            Assert.Throws<LauncherException>(() => RunAs.Run(user, password + "x", $"\"{Paths.Cmd}\" /c exit 0", hidden: true, wait: true));
            ScriptRunner.Run(profile, password, "it-ok", $"if ($env:USERNAME -ne {ScriptRunner.Quote(user)}) {{ throw 'wrong user' }}", TimeSpan.FromMinutes(1));
            var failed = Assert.Throws<ScriptFailedException>(() => ScriptRunner.Run(profile, password, "it-fail", "throw 'boom-it'", TimeSpan.FromMinutes(1)));
            Assert.Contains("boom-it", failed.Message);
            Assert.Throws<LauncherException>(() => RunAs.Run(user, password, $"\"{Paths.Cmd}\" /d /c ping -n 30 127.0.0.1 >nul", hidden: true, wait: true, TimeSpan.FromSeconds(1)));

            // The work folder is trusted, and the profile user can't remove the sentinel that keeps it from being
            // emptied and turned into a junction, nor the folder itself.
            Assert.True(Paths.IsTrustedProfileWork(user));
            var sentinel = SecureFolder.SentinelPath(work);
            Assert.Throws<ScriptFailedException>(() => ScriptRunner.Run(profile, password, "it-sentinel",
                $"Remove-Item -LiteralPath {ScriptRunner.Quote(sentinel)} -Force", TimeSpan.FromMinutes(1)));
            Assert.Throws<ScriptFailedException>(() => ScriptRunner.Run(profile, password, "it-workdir",
                $"[IO.Directory]::Delete({ScriptRunner.Quote(work)}, $true)", TimeSpan.FromMinutes(1)));
            Assert.True(File.Exists(sentinel));
            Assert.True(Paths.IsTrustedProfileWork(user));

            // The profile user can actually write to the shared folder.
            ScriptRunner.Run(profile, password, "it-write", $"Set-Content -LiteralPath {ScriptRunner.Quote(Path.Combine(shared.Path, "from-profile.txt"))} -Value ok", TimeSpan.FromMinutes(1));
            Assert.True(File.Exists(Path.Combine(shared.Path, "from-profile.txt")));

            // Git
            if (TestEnv.FindOnPath("git.exe") is not null)
            {
                ProfileSetup.ConfigureGit(profile, password);
                var gitconfig = File.ReadAllText(Path.Combine(Environment.ExpandEnvironmentVariables(ProfileImagePath(sid!)!), ".gitconfig"));
                Assert.Contains(shared.Path.Replace('\\', '/'), gitconfig);
                Assert.Contains("it@example.com", gitconfig);
            }

            // Optional: Claude Desktop inside the profile
            if (Environment.GetEnvironmentVariable("CMA_ADMIN_TESTS_CLAUDE") == "1")
            {
                var install = ClaudeDesktop.Detect();
                Assert.SkipWhen(install.Kind != InstallKind.Msix, "Claude Desktop (MSIX) is not installed for this account.");
                try
                {
                    ProfileSetup.RegisterClaude(profile, password, install, null, store);
                }
                catch (ScriptFailedException)
                {
                    var provisioned = Elevated(new ElevatedRequest { Op = ElevatedOp.Provision, PackageFamilyName = install.FamilyName! });
                    Assert.True(provisioned.Ok, provisioned.Error);
                    ProfileSetup.RegisterClaude(profile, password, install, null, store);
                }
                Assert.Equal(install.Version, profile.RegisteredClaudeVersion);
                ClaudeDesktop.LaunchForProfile(profile, password);
                Thread.Sleep(TimeSpan.FromSeconds(10));
                ProcessRunner.Run("tasklist.exe", new[] { "/nh", "/fi", $"username eq {user}", "/fi", "imagename eq claude.exe" }, out var tasks);
                Assert.Contains("claude.exe", tasks, StringComparison.OrdinalIgnoreCase);
            }

            // Update: add/revoke folders, admin on and off
            var updated = Elevated(new ElevatedRequest { Op = ElevatedOp.Update, UserName = user, Folders = { later.Path }, RevokeFolders = { shared.Path }, Admin = true });
            Assert.True(updated.Ok, updated.Error);
            Assert.True(LocalUsers.HasModifyAccess(later.Path, sid!));
            Assert.False(LocalUsers.HasModifyAccess(shared.Path, sid!));
            Assert.True(InGroup(user, "S-1-5-32-544"));
            Assert.True(Elevated(new ElevatedRequest { Op = ElevatedOp.Update, UserName = user, Admin = false }).Ok);
            Assert.False(InGroup(user, "S-1-5-32-544"));

            // Secure recreates a missing work folder (Repair)
            Directory.Delete(work, recursive: true);
            Assert.True(Elevated(new ElevatedRequest { Op = ElevatedOp.Secure, UserName = user }).Ok);
            Assert.True(Directory.Exists(work));

            // Delete
            var profilePath = Environment.ExpandEnvironmentVariables(ProfileImagePath(sid!)!);
            var deleted = Elevated(new ElevatedRequest { Op = ElevatedOp.Delete, UserName = user, Folders = { later.Path } });
            Assert.True(deleted.Ok, deleted.Error);
            Assert.Null(LocalUsers.TryGetSid(user));
            Assert.False(IsHidden(user));
            Assert.False(Directory.Exists(work));
            Assert.False(LocalUsers.HasModifyAccess(later.Path, sid!));
            Assert.True(deleted.Warning is not null || !Directory.Exists(profilePath), deleted.Warning);
            sid = null;
        }
        finally
        {
            if (LocalUsers.Exists(user))
                Elevated(new ElevatedRequest { Op = ElevatedOp.Delete, UserName = user, Folders = { shared.Path, later.Path } });
        }
    }

    // The scenarios below use a throwaway root next to the real one, never the app's own folder.
    static readonly string Guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null).Value;

    static string NewTestRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "cma-it-" + Guid.NewGuid().ToString("N")[..8]);

    static string OwnerOf(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!.Value;

    static void CleanUpTestRoot(string root)
    {
        foreach (var entry in new DirectoryInfo(Path.GetDirectoryName(root)!).GetDirectories(Path.GetFileName(root) + "*"))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                entry.Delete(); // the link only
            else
                entry.Delete(recursive: true);
        }
    }

    [Fact]
    public void Planted_junction_root_is_moved_aside_and_its_target_is_untouched()
    {
        RequireAdminOptIn();
        var root = NewTestRoot();
        using var target = new TempDir();
        Directory.CreateDirectory(Path.Combine(target.Path, "claude-x"));
        File.WriteAllText(Path.Combine(target.Path, "claude-x", "canary.txt"), "keep me");
        SecureFolderTests.SetOwner(target.Path, TestEnv.CurrentSid);
        var aclBefore = new DirectoryInfo(target.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        SecureFolderTests.MakeJunction(root, target.Path);
        try
        {
            Elevation.SecureWorkFolder(root, "claude-x", Guests, TestEnv.CurrentSid);

            // Nothing happened through the junction.
            Assert.Equal(aclBefore, new DirectoryInfo(target.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.True(File.Exists(Path.Combine(target.Path, "claude-x", "canary.txt")));
            Assert.False(File.Exists(SecureFolder.SentinelPath(Path.Combine(target.Path, "claude-x"))));

            // A real admin-owned root replaced it, and the junction was only renamed.
            Assert.Equal(FolderTrust.Trusted, SecureFolder.Inspect(root));
            Assert.True(SecureFolder.IsTrustedWorkFolder(root, Path.Combine(root, "claude-x")));
            var aside = Assert.Single(new DirectoryInfo(Path.GetDirectoryName(root)!).GetDirectories(Path.GetFileName(root) + ".untrusted-*"));
            Assert.True(aside.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            CleanUpTestRoot(root);
        }
    }

    [Fact]
    public void Root_pre_created_by_another_account_is_moved_aside()
    {
        RequireAdminOptIn();
        var root = NewTestRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "planted.cmd"), "echo planted");
        SecureFolderTests.SetOwner(root, TestEnv.CurrentSid); // not Administrators: not created by the app
        try
        {
            Elevation.SecureWorkFolder(root, "claude-x", Guests, TestEnv.CurrentSid);

            Assert.Equal("S-1-5-32-544", OwnerOf(root));
            Assert.False(File.Exists(Path.Combine(root, "planted.cmd")));
            Assert.True(SecureFolder.IsTrustedWorkFolder(root, Path.Combine(root, "claude-x")));
            var aside = Assert.Single(new DirectoryInfo(Path.GetDirectoryName(root)!).GetDirectories(Path.GetFileName(root) + ".untrusted-*"));
            Assert.True(File.Exists(Path.Combine(aside.FullName, "planted.cmd")));
        }
        finally
        {
            CleanUpTestRoot(root);
        }
    }

    [Fact]
    public void Work_folder_turned_into_a_junction_is_replaced_without_following_it()
    {
        RequireAdminOptIn();
        var root = NewTestRoot();
        using var target = new TempDir();
        File.WriteAllText(target.File("canary.txt"), "keep me");
        try
        {
            Elevation.SecureWorkFolder(root, "claude-x", Guests, TestEnv.CurrentSid);
            var work = Path.Combine(root, "claude-x");
            Directory.Delete(work, recursive: true);
            SecureFolderTests.MakeJunction(work, target.Path);
            Assert.False(SecureFolder.IsTrustedWorkFolder(root, work));

            Elevation.SecureWorkFolder(root, "claude-x", Guests, TestEnv.CurrentSid);

            Assert.True(File.Exists(target.File("canary.txt")));
            Assert.True(SecureFolder.IsTrustedWorkFolder(root, work));
            Assert.Equal(FolderTrust.Trusted, SecureFolder.Inspect(root)); // the existing good root was kept
            Assert.Empty(new DirectoryInfo(Path.GetDirectoryName(root)!).GetDirectories(Path.GetFileName(root) + ".untrusted-*"));
        }
        finally
        {
            CleanUpTestRoot(root);
        }
    }

    [Fact]
    public void Failed_create_leaves_nothing_behind()
    {
        RequireAdminOptIn();
        var user = "claude-it-" + Guid.NewGuid().ToString("N")[..6];
        var result = Elevated(new ElevatedRequest
        {
            Op = ElevatedOp.Create, UserName = user, ProtectedPassword = Secret.Protect(Secret.GeneratePassword()),
            Folders = { @"C:\definitely\missing\folder" },
        });
        Assert.False(result.Ok);
        Assert.False(LocalUsers.Exists(user));
        Assert.False(IsHidden(user));
        Assert.False(Directory.Exists(Paths.ProfileWork(user)));
    }
}
