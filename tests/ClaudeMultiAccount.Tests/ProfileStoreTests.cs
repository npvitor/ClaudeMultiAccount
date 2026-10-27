using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeMultiAccount.Core;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class ProfileStoreTests
{
    [Fact]
    public void Missing_file_gives_empty_store()
    {
        using var dir = new TempDir();
        var store = ProfileStore.Load(dir.File("profiles.json"));
        Assert.Empty(store.Profiles);
        Assert.False(File.Exists(dir.File("profiles.json.corrupt")));
    }

    [Fact]
    public void Save_and_load_round_trip_every_field()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "sub", "profiles.json"); // folder is created on save
        var store = ProfileStore.Load(file);
        var original = new Profile
        {
            DisplayName = "João \"Pessoal\" ✓",
            WindowsUser = "claude-joao",
            Color = "#DC2626",
            ProtectedPassword = Secret.Protect("pw"),
            SharedFolders = { @"C:\Git", @"D:\repos com espaço" },
            GitName = "João",
            GitEmail = "joao@example.com",
            IsAdmin = true,
            RegisteredClaudeVersion = "1.2.3.0",
        };
        store.Add(original);
        store.Save();

        var loaded = Assert.Single(ProfileStore.Load(file).Profiles);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.DisplayName, loaded.DisplayName);
        Assert.Equal(original.WindowsUser, loaded.WindowsUser);
        Assert.Equal(original.Color, loaded.Color);
        Assert.Equal(original.ProtectedPassword, loaded.ProtectedPassword);
        Assert.Equal(original.SharedFolders, loaded.SharedFolders);
        Assert.Equal(original.GitName, loaded.GitName);
        Assert.Equal(original.GitEmail, loaded.GitEmail);
        Assert.Equal(original.IsAdmin, loaded.IsAdmin);
        Assert.Equal(original.RegisteredClaudeVersion, loaded.RegisteredClaudeVersion);
        Assert.Equal(original.CreatedAt, loaded.CreatedAt);
        Assert.Equal("pw", Secret.Unprotect(loaded.ProtectedPassword));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Remove_then_save_persists_removal()
    {
        using var dir = new TempDir();
        var file = dir.File("profiles.json");
        var store = ProfileStore.Load(file);
        var a = new Profile { DisplayName = "A" };
        var b = new Profile { DisplayName = "B" };
        store.Add(a);
        store.Add(b);
        store.Save();
        store.Remove(a);
        store.Save();
        Assert.Equal(new[] { "B" }, ProfileStore.Load(file).Profiles.Select(p => p.DisplayName));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"an\":\"object, not a list\"}")]
    public void Corrupt_file_is_kept_aside_and_store_starts_empty(string content)
    {
        using var dir = new TempDir();
        var file = dir.File("profiles.json");
        File.WriteAllText(file, content);

        var store = ProfileStore.Load(file);

        Assert.Empty(store.Profiles);
        Assert.Equal(content, File.ReadAllText(file + ".corrupt"));
    }

    [Fact]
    public void Json_null_gives_empty_store()
    {
        using var dir = new TempDir();
        var file = dir.File("profiles.json");
        File.WriteAllText(file, "null");
        Assert.Empty(ProfileStore.Load(file).Profiles);
    }

    [Fact]
    public void Older_files_without_new_fields_still_load()
    {
        using var dir = new TempDir();
        var file = dir.File("profiles.json");
        File.WriteAllText(file, """[{"DisplayName":"Old","WindowsUser":"claude-old","ProtectedPassword":"x"}]""");
        var p = Assert.Single(ProfileStore.Load(file).Profiles);
        Assert.Equal("Old", p.DisplayName);
        Assert.False(p.IsAdmin);
        Assert.Null(p.RegisteredClaudeVersion);
        Assert.Empty(p.SharedFolders);
        Assert.Equal("#4F46E5", p.Color);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_the_file()
    {
        using var dir = new TempDir();
        var file = dir.File("profiles.json");
        var store = ProfileStore.Load(file);
        for (int i = 0; i < 5; i++)
            store.Add(new Profile { DisplayName = "P" + i });

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(store.Save)));

        Assert.Equal(5, ProfileStore.Load(file).Profiles.Count);
        Assert.False(File.Exists(file + ".corrupt"));
    }

    [Fact]
    public void New_profiles_get_unique_ids()
    {
        Assert.NotEqual(new Profile().Id, new Profile().Id);
        Assert.Equal(32, new Profile().Id.Length);
    }
}
