using System;
using System.IO;
using System.Text.Json;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Xunit;

namespace ClaudeMultiAccount.Tests;

/// <summary>
/// The elevated entry point's guards. None of these reach a system change, so they run without admin rights.
/// </summary>
public class ElevationTests
{
    static (int Code, ElevatedResult Result) RunElevated(ElevatedRequest request)
    {
        using var dir = new TempDir();
        var requestPath = dir.File("r.request.json");
        var resultPath = dir.File("r.result.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        var code = Elevation.RunElevated(requestPath, resultPath);
        return (code, JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(resultPath))!);
    }

    [Fact]
    public void Request_from_another_account_is_refused()
    {
        var (code, result) = RunElevated(new ElevatedRequest { Op = ElevatedOp.Delete, UserName = "claude-x", RequesterSid = "S-1-5-21-1-2-3-1001" });
        Assert.Equal(1, code);
        Assert.False(result.Ok);
        Assert.Equal(S.ErrElevatedOtherUser, result.Error);
    }

    [Theory]
    [InlineData(ElevatedOp.Delete, "Administrator")]
    [InlineData(ElevatedOp.Delete, "Administrador")]
    [InlineData(ElevatedOp.Delete, "")]
    [InlineData(ElevatedOp.Delete, "claude-x\" /IM *")]
    [InlineData(ElevatedOp.Update, "Guest")]
    [InlineData(ElevatedOp.Secure, "..\\..\\Windows")]
    [InlineData(ElevatedOp.Create, "claude-name-way-too-long-for-windows")]
    public void Ops_on_accounts_the_app_did_not_create_are_refused(string op, string user)
    {
        var (code, result) = RunElevated(new ElevatedRequest { Op = op, UserName = user, RequesterSid = TestEnv.CurrentSid });
        Assert.Equal(1, code);
        Assert.Equal(string.Format(S.ErrInvalidProfileUser, user), result.Error);
    }

    [Fact]
    public void Deleting_the_requesting_account_itself_is_refused()
    {
        var me = Environment.UserName;
        var (code, result) = RunElevated(new ElevatedRequest { Op = ElevatedOp.Delete, UserName = me, RequesterSid = TestEnv.CurrentSid });
        Assert.Equal(1, code);
        Assert.Equal(string.Format(S.ErrInvalidProfileUser, me), result.Error);
    }

    [Fact]
    public void Unknown_operation_is_refused()
    {
        var (code, result) = RunElevated(new ElevatedRequest { Op = "format-c", UserName = "claude-nouser-zz", RequesterSid = TestEnv.CurrentSid });
        Assert.Equal(1, code);
        Assert.Contains("format-c", result.Error);
    }

    [Theory]
    [InlineData("Claude'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("Claude_short")]
    [InlineData("")]
    public void Provision_with_invalid_family_name_never_runs_powershell(string family)
    {
        var (code, result) = RunElevated(new ElevatedRequest { Op = ElevatedOp.Provision, PackageFamilyName = family, RequesterSid = TestEnv.CurrentSid });
        Assert.Equal(1, code);
        Assert.Contains("Invalid package family name", result.Error);
    }

    [Fact]
    public void Missing_or_broken_request_file_reports_an_error()
    {
        using var dir = new TempDir();
        Assert.Equal(1, Elevation.RunElevated(dir.File("missing.json"), dir.File("a.json")));
        Assert.False(JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(dir.File("a.json")))!.Ok);

        File.WriteAllText(dir.File("bad.json"), "{");
        Assert.Equal(1, Elevation.RunElevated(dir.File("bad.json"), dir.File("b.json")));
        Assert.NotNull(JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(dir.File("b.json")))!.Error);
    }

    [Fact]
    public void Request_survives_the_json_round_trip()
    {
        var request = new ElevatedRequest
        {
            Op = ElevatedOp.Update, RequesterSid = "S-1-5-21-1", UserName = "claude-a", FullName = "Claude - Á",
            ProtectedPassword = "abc==", PackageFamilyName = "Claude_pzs8sxrjxfjjc",
            Folders = { @"C:\Git" }, RevokeFolders = { @"D:\old" }, Admin = false,
        };
        var copy = JsonSerializer.Deserialize<ElevatedRequest>(JsonSerializer.Serialize(request))!;
        Assert.Equivalent(request, copy, strict: true);
        Assert.Null(JsonSerializer.Deserialize<ElevatedRequest>("{}")!.Admin); // null = leave membership alone
    }

    [Theory]
    [InlineData("claude-pessoal", true)]
    [InlineData("claude-a2", true)]
    [InlineData("Claude-Trabalho", true)]
    [InlineData("claude-12345678901234", false)] // 21 chars
    [InlineData("claude-", false)]
    [InlineData("claude-ção", false)]
    [InlineData("claude-a b", false)]
    [InlineData("xclaude-a", false)]
    [InlineData(null, false)]
    public void IsProfileUserName(string? name, bool expected) => Assert.Equal(expected, Elevation.IsProfileUserName(name));

    [Theory]
    [InlineData("Claude_pzs8sxrjxfjjc", true)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", true)]
    [InlineData("Claude_PZS8SXRJXFJJC", false)]
    [InlineData("Claude pzs8sxrjxfjjc", false)]
    public void IsPackageFamilyName(string name, bool expected) => Assert.Equal(expected, Elevation.IsPackageFamilyName(name));
}

public class StartupRegistrationTests : IDisposable
{
    readonly string _key = @"Software\ClaudeMultiAccount.Tests\" + Guid.NewGuid().ToString("N");
    readonly string _original = StartupRegistration.RunKey;

    public StartupRegistrationTests() => StartupRegistration.RunKey = _key;

    public void Dispose()
    {
        StartupRegistration.RunKey = _original;
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ClaudeMultiAccount.Tests", throwOnMissingSubKey: false);
    }

    [Fact]
    public void Enable_and_disable()
    {
        Assert.False(StartupRegistration.IsEnabled);

        StartupRegistration.Set(true);
        Assert.True(StartupRegistration.IsEnabled);
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_key)!)
            Assert.Equal($"\"{Environment.ProcessPath}\" --tray", key.GetValue("ClaudeMultiAccount"));

        StartupRegistration.Set(false);
        Assert.False(StartupRegistration.IsEnabled);
        StartupRegistration.Set(false); // disabling twice is fine
    }

    [Fact]
    public void Default_key_is_the_users_run_key() =>
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", _original);
}
