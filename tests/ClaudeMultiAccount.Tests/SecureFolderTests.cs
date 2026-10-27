using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Xunit;

namespace ClaudeMultiAccount.Tests;

/// <summary>Trust checks for the ProgramData folders. Runs without admin rights.</summary>
public class SecureFolderTests
{
    internal static void MakeJunction(string link, string target)
    {
        var (code, output) = TestEnv.Run(Paths.Cmd, new[] { "/d", "/c", "mklink", "/J", link, target });
        Assert.True(code == 0, output);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    internal static void SetOwner(string path, string sid)
    {
        var info = new DirectoryInfo(path);
        var acl = info.GetAccessControl(AccessControlSections.Owner);
        acl.SetOwner(new SecurityIdentifier(sid));
        info.SetAccessControl(acl);
    }

    [Fact]
    public void Missing_path_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "cma-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(FolderTrust.Missing, SecureFolder.Inspect(path));
        Assert.Null(SecureFolder.Open(path));
    }

    [Fact]
    public void Folder_owned_by_a_normal_user_is_untrusted()
    {
        using var dir = new TempDir();
        SetOwner(dir.Path, TestEnv.CurrentSid); // explicit: elevated runs would otherwise default to Administrators
        Assert.Equal(FolderTrust.Untrusted, SecureFolder.Inspect(dir.Path));
    }

    [Fact]
    public void System_owned_folder_is_trusted()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.SkipUnless(new DirectoryInfo(programData).GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier))!.Value is "S-1-5-18" or "S-1-5-32-544", "ProgramData has an unusual owner here");
        Assert.Equal(FolderTrust.Trusted, SecureFolder.Inspect(programData));
    }

    [Fact]
    public void Junction_is_untrusted_even_when_it_points_to_a_trusted_folder()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        using var dir = new TempDir();
        var link = dir.File("link");
        MakeJunction(link, programData);
        try
        {
            Assert.Equal(FolderTrust.Untrusted, SecureFolder.Inspect(link));
            Assert.False(SecureFolder.IsTrustedWorkFolder(link, Path.Combine(link, "x")));
        }
        finally
        {
            Directory.Delete(link); // the link only
        }
    }

    [Fact]
    public void File_where_a_folder_is_expected_and_the_reverse_are_untrusted()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("f"), "x");
        Assert.Equal(FolderTrust.Untrusted, SecureFolder.Inspect(dir.File("f"), directory: true));
        Assert.Equal(FolderTrust.Untrusted, SecureFolder.Inspect(dir.Path, directory: false));
    }

    [Fact]
    public void Recursive_delete_of_a_junction_removes_only_the_link()
    {
        // The elevated step relies on this when it clears a work folder a profile user turned into a junction.
        using var target = new TempDir();
        File.WriteAllText(target.File("canary.txt"), "keep me");
        using var dir = new TempDir();
        var link = dir.File("work");
        MakeJunction(link, target.Path);

        Directory.Delete(link, recursive: true);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(target.File("canary.txt")));
    }

    [Fact]
    public void Work_folder_in_temp_is_never_trusted()
    {
        using var root = new TempDir();
        var work = Directory.CreateDirectory(root.File("claude-x")).FullName;
        File.WriteAllText(SecureFolder.SentinelPath(work), "fake");
        SetOwner(root.Path, TestEnv.CurrentSid);
        Assert.False(SecureFolder.IsTrustedWorkFolder(root.Path, work));
    }

    [Fact]
    public void Untrusted_or_missing_work_folder_asks_for_repair()
    {
        var user = "claude-nowork-" + Guid.NewGuid().ToString("N")[..5];
        Assert.False(Paths.IsTrustedProfileWork(user));
        Assert.Equal(S.ErrWorkFolderMissing, Assert.Throws<LauncherException>(() => Paths.RequireProfileWork(user)).Message);
    }
}
