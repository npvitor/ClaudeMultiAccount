using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

/// <summary>Setup steps that run as the profile user: Windows profile, Claude registration, Git.</summary>
public static class ProfileSetup
{
    /// <summary>The first logon creates C:\Users\&lt;user&gt;; it can take a little while.</summary>
    public static void PrepareWindowsProfile(Profile profile, string password)
    {
        var code = RunAs.Run(profile.WindowsUser, password, $"\"{Paths.Cmd}\" /d /c exit 0", hidden: true, wait: true, TimeSpan.FromMinutes(5));
        if (code != 0)
            throw new LauncherException(string.Format(S.ErrStep, $"exit code {code}"));
    }

    /// <summary>
    /// Makes Claude Desktop available to the profile user. For MSIX installs the package already on the
    /// machine is registered for that user (no download). Otherwise <paramref name="installerPath"/> runs inside the profile.
    /// </summary>
    public static void RegisterClaude(Profile profile, string password, ClaudeInstall install, string? installerPath, ProfileStore store)
    {
        if (install.Kind == InstallKind.Msix)
        {
            var manifest = Path.Combine(install.Location!, "AppxManifest.xml");
            ScriptRunner.Run(profile, password, "register-claude",
                $"Add-AppxPackage -Register {ScriptRunner.Quote(manifest)} -DisableDevelopmentMode -ForceApplicationShutdown\n" +
                "if (-not (Get-AppxPackage -Name Claude)) { throw 'Claude package was not registered for this user.' }",
                TimeSpan.FromMinutes(10));
            profile.RegisteredClaudeVersion = install.Version;
            store.Save();
            return;
        }

        if (installerPath is null)
            throw new LauncherException(S.ErrNeedInstaller);

        // Copy the installer to the profile's locked-down work folder, which the profile user can read.
        var shared = Path.Combine(Paths.RequireProfileWork(profile.WindowsUser), Path.GetFileName(installerPath));
        File.Copy(installerPath, shared, overwrite: true);
        try
        {
            var ext = Path.GetExtension(shared).ToLowerInvariant();
            if (ext is ".msix" or ".msixbundle" or ".appx")
            {
                ScriptRunner.Run(profile, password, "install-claude",
                    $"Add-AppxPackage -Path {ScriptRunner.Quote(shared)} -ForceApplicationShutdown", TimeSpan.FromMinutes(10));
            }
            else
            {
                var code = RunAs.Run(profile.WindowsUser, password, $"\"{shared}\"", hidden: false, wait: true, TimeSpan.FromMinutes(15));
                if (code != 0)
                    throw new LauncherException(string.Format(S.ErrStep, $"installer exit code {code}"));
            }
        }
        finally
        {
            Paths.TryDelete(shared);
        }
        profile.RegisteredClaudeVersion = "installer";
        store.Save();
    }

    /// <summary>
    /// <see cref="RegisterClaude"/>, plus a fallback for MSIX: the Claude package ships a Windows service, so
    /// Windows may refuse to register it for a standard user. In that case the package is provisioned for
    /// all users (one UAC prompt) and the registration is tried once more.
    /// </summary>
    public static async Task RegisterClaudeAsync(Profile profile, string password, ClaudeInstall install, string? installerPath, ProfileStore store)
    {
        try
        {
            await Task.Run(() => RegisterClaude(profile, password, install, installerPath, store));
        }
        catch (ScriptFailedException first) when (install.Kind == InstallKind.Msix)
        {
            var provisioned = await Elevation.RunAsync(new ElevatedRequest
            {
                Op = ElevatedOp.Provision,
                PackageFamilyName = install.FamilyName!,
            });
            if (provisioned is null)
                throw new LauncherException(first.Message);
            try
            {
                await Task.Run(() => RegisterClaude(profile, password, install, installerPath, store));
            }
            catch (ScriptFailedException second)
            {
                throw new LauncherException(string.Format(S.ErrMsixRegister, second.Message));
            }
        }
    }

    /// <summary>
    /// When the main account's Claude updates (MSIX), the profile keeps the old version until it is
    /// registered again. Called before every launch; does nothing when versions already match.
    /// </summary>
    public static Task EnsureUpToDateAsync(Profile profile, string password, ClaudeInstall install, ProfileStore store, IProgress<string>? progress)
    {
        if (install.Kind != InstallKind.Msix || profile.RegisteredClaudeVersion == install.Version)
            return Task.CompletedTask;
        progress?.Report(string.Format(S.StatusUpdatingProfile, profile.DisplayName));
        return RegisterClaudeAsync(profile, password, install, null, store);
    }

    /// <summary>Marks the shared folders as safe for Git and sets the commit identity, if Git is installed.</summary>
    public static void ConfigureGit(Profile profile, string password)
    {
        var body = new StringBuilder()
            .AppendLine("if (-not (Get-Command git -ErrorAction SilentlyContinue)) { exit 0 }")
            // Native commands write to stderr; don't let that abort the script.
            .AppendLine("$ErrorActionPreference = 'Continue'")
            .AppendLine("$existing = @(git config --global --get-all safe.directory)");

        foreach (var folder in profile.SharedFolders)
        {
            var path = folder.Replace('\\', '/').TrimEnd('/');
            // The repo folders belong to the main account, so Git would refuse them as "dubious ownership".
            foreach (var entry in new[] { path, path + "/*" })
                body.AppendLine($"if ($existing -notcontains {ScriptRunner.Quote(entry)}) {{ git config --global --add safe.directory {ScriptRunner.Quote(entry)} }}");
        }
        if (!string.IsNullOrWhiteSpace(profile.GitName))
            body.AppendLine($"git config --global user.name {ScriptRunner.Quote(profile.GitName)}");
        if (!string.IsNullOrWhiteSpace(profile.GitEmail))
            body.AppendLine($"git config --global user.email {ScriptRunner.Quote(profile.GitEmail)}");

        ScriptRunner.Run(profile, password, "git", body.ToString(), TimeSpan.FromMinutes(2));
    }
}
