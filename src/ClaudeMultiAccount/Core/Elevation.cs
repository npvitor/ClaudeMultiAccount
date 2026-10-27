using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.Core;

/// <summary>Operations the elevated copy of the app can run.</summary>
public static class ElevatedOp
{
    public const string Create = "create";
    public const string Delete = "delete";
    public const string Folders = "folders";
    public const string Secure = "secure";
    public const string Provision = "provision";
}

public sealed class ElevatedRequest
{
    public string Op { get; set; } = "";
    public string RequesterSid { get; set; } = "";
    public string UserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public List<string> RevokeFolders { get; set; } = new();
}

public sealed class ElevatedResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Set when the operation succeeded but left something the user should know about.</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Runs admin-only operations by relaunching this exe elevated ("--elevated request result").
/// The request travels in a temp file, never on the command line, and the password inside it
/// stays DPAPI-encrypted: only the same Windows user (now elevated) can read it.
/// </summary>
public static class Elevation
{
    public const string Flag = "--elevated";

    /// <summary>Returns null when the user dismisses the UAC prompt; throws on failure.</summary>
    public static async Task<ElevatedResult?> RunAsync(ElevatedRequest request)
    {
        request.RequesterSid = WindowsIdentity.GetCurrent().User!.Value;
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(Paths.Temp, id + ".request.json");
        var resultPath = Path.Combine(Paths.Temp, id + ".result.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{Flag} \"{requestPath}\" \"{resultPath}\"",
            };
            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                return null;
            }
            if (process is null)
                throw new LauncherException(S.ErrElevatedNoResult);
            using (process)
                await process.WaitForExitAsync();

            if (!File.Exists(resultPath))
                throw new LauncherException(S.ErrElevatedNoResult);
            var result = JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(resultPath));
            if (result is null || !result.Ok)
                throw new LauncherException(result?.Error ?? S.ErrElevatedNoResult);
            return result;
        }
        finally
        {
            Paths.TryDelete(requestPath);
            Paths.TryDelete(resultPath);
        }
    }

    /// <summary>Entry point of the elevated copy of the app. Returns the process exit code.</summary>
    public static int RunElevated(string requestPath, string resultPath)
    {
        var result = new ElevatedResult();
        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestPath))
                          ?? throw new LauncherException(S.ErrElevatedNoResult);

            // DPAPI data only opens for the same user, so an admin from another account can't continue.
            if (request.RequesterSid != WindowsIdentity.GetCurrent().User?.Value)
                throw new LauncherException(S.ErrElevatedOtherUser);

            switch (request.Op)
            {
                case ElevatedOp.Create: Create(request); break;
                case ElevatedOp.Delete: result.Warning = Delete(request); break;
                case ElevatedOp.Folders: Folders(request); break;
                case ElevatedOp.Secure: SecureWorkFolder(request.UserName, SidOf(request.UserName), request.RequesterSid); break;
                case ElevatedOp.Provision: Provision(request.PackageFamilyName); break;
                default: throw new LauncherException($"Unknown operation '{request.Op}'.");
            }
            result.Ok = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        try { File.WriteAllText(resultPath, JsonSerializer.Serialize(result)); } catch { }
        return result.Ok ? 0 : 1;
    }

    static void Create(ElevatedRequest r)
    {
        var password = Secret.Unprotect(r.ProtectedPassword);
        LocalUsers.Create(r.UserName, password, r.FullName);

        string? sid = null;
        var granted = new List<string>();
        try
        {
            LocalUsers.AddToUsersGroup(r.UserName);
            LocalUsers.SetHidden(r.UserName, true);
            sid = SidOf(r.UserName);
            foreach (var folder in r.Folders)
            {
                LocalUsers.GrantFolder(folder, sid);
                granted.Add(folder);
            }
            SecureWorkFolder(r.UserName, sid, r.RequesterSid);
        }
        catch
        {
            // Undo everything: a failed attempt must not leave a hidden account behind,
            // because the profile isn't saved yet and the app couldn't offer to remove it.
            if (sid is { } userSid)
                foreach (var folder in granted)
                    Try(() => LocalUsers.RevokeFolder(folder, userSid));
            Try(() => DeleteWorkFolder(r.UserName));
            Try(() => LocalUsers.Delete(r.UserName));
            Try(() => LocalUsers.SetHidden(r.UserName, false));
            throw;
        }
    }

    static string? Delete(ElevatedRequest r)
    {
        var sid = LocalUsers.TryGetSid(r.UserName);
        LocalUsers.KillProcesses(r.UserName);
        if (sid is not null)
            foreach (var folder in r.Folders)
                LocalUsers.RevokeFolder(folder, sid);
        LocalUsers.Delete(r.UserName);
        LocalUsers.SetHidden(r.UserName, false);
        Try(() => DeleteWorkFolder(r.UserName));

        var leftover = sid is null ? null : LocalUsers.DeleteWindowsProfile(sid);
        return leftover is null ? null : string.Format(S.WarnProfileLeftover, leftover);
    }

    static void Folders(ElevatedRequest r)
    {
        var sid = SidOf(r.UserName);
        foreach (var folder in r.RevokeFolders)
            LocalUsers.RevokeFolder(folder, sid);
        foreach (var folder in r.Folders)
            LocalUsers.GrantFolder(folder, sid);
    }

    /// <summary>
    /// (Re)creates the profile's work folder with an explicit ACL: admins, the launcher's user and the
    /// profile user only. The ProgramData root is locked too, so no other account can pre-create the
    /// folder with a loose ACL and swap a script or installer that later runs inside the profile.
    /// </summary>
    static void SecureWorkFolder(string userName, string userSid, string requesterSid)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        try
        {
            var root = Directory.CreateDirectory(Paths.Shared);
            var rootAcl = new DirectorySecurity();
            rootAcl.SetOwner(admins);
            rootAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            rootAcl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            rootAcl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            rootAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            root.SetAccessControl(rootAcl);

            // Start from an empty folder: anything already inside wasn't created under this ACL.
            var work = new DirectoryInfo(Paths.ProfileWork(userName));
            if (work.Exists)
                work.Delete(recursive: true);
            work.Create();
            var acl = new DirectorySecurity();
            acl.SetOwner(admins);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(requesterSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(userSid), FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            work.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new LauncherException(string.Format(S.ErrSecureFolder, Paths.Shared, ex.Message));
        }
    }

    static void DeleteWorkFolder(string userName)
    {
        var work = Paths.ProfileWork(userName);
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
    }

    /// <summary>
    /// Provisions the installed Claude package for every user of the machine. Packages that ship a
    /// Windows service can refuse a registration by a standard user; once provisioned, the profile
    /// user can register it.
    /// </summary>
    static void Provision(string familyName)
    {
        if (!Regex.IsMatch(familyName, @"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$"))
            throw new LauncherException($"Invalid package family name '{familyName}'.");

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $null = [Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType = WindowsRuntime]
            $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
                $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperationWithProgress`2' } | Select-Object -First 1
            $manager = New-Object Windows.Management.Deployment.PackageManager
            $operation = $manager.ProvisionPackageForAllUsersAsync('{{familyName}}')
            $task = $asTask.MakeGenericMethod([Windows.Management.Deployment.DeploymentResult], [Windows.Management.Deployment.DeploymentProgress]).Invoke($null, @($operation))
            $result = $task.GetAwaiter().GetResult()
            if ($result.ExtendedErrorCode) { throw $result.ErrorText }
            """;
        var code = ProcessRunner.Run(Paths.PowerShell,
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            out var output, TimeSpan.FromMinutes(10));
        if (code != 0)
            throw new LauncherException(string.Format(S.ErrProvision, output.Trim()));
    }

    static string SidOf(string userName) =>
        LocalUsers.TryGetSid(userName) ?? throw new LauncherException(string.Format(S.ErrUserMissing, userName));

    static void Try(Action action)
    {
        try { action(); } catch { }
    }
}
