using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClaudeMultiAccount.Core;

public enum FolderTrust { Missing, Trusted, Untrusted }

/// <summary>
/// Checks the folders that hold scripts the app runs inside profiles. Standard users can create entries in
/// ProgramData, so a path there is trusted only when it is the entry itself (never a junction or link, which are
/// not followed) and is owned by Administrators or SYSTEM, which no standard user can fake.
/// </summary>
internal static class SecureFolder
{
    /// <summary>
    /// Admin-owned file in every work folder. The profile user can't delete it, so it can't empty its work folder,
    /// and only an empty folder can be removed or turned into a junction.
    /// </summary>
    const string SentinelName = ".claude-multi-account";

    static readonly string[] TrustedOwners = { "S-1-5-32-544", "S-1-5-18" };

    public static string SentinelPath(string workFolder) => Path.Combine(workFolder, SentinelName);

    /// <summary>
    /// Opens the entry at <paramref name="path"/> itself. Null when nothing is there. Without
    /// <paramref name="allowDelete"/>, nobody can rename or delete the entry while the handle is open.
    /// </summary>
    public static SafeFileHandle? Open(string path, bool allowDelete = true)
    {
        var handle = Native.CreateFileW(path, Native.READ_CONTROL | Native.FILE_READ_ATTRIBUTES,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | (allowDelete ? Native.FILE_SHARE_DELETE : 0),
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is Native.ERROR_FILE_NOT_FOUND or Native.ERROR_PATH_NOT_FOUND)
            return null;
        throw new Win32Exception(error);
    }

    public static FolderTrust Inspect(SafeFileHandle? handle, bool directory)
    {
        if (handle is null)
            return FolderTrust.Missing;
        if (!Native.GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes = (FileAttributes)info.dwFileAttributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            return FolderTrust.Untrusted;
        return TrustedOwners.Contains(OwnerOf(handle)) ? FolderTrust.Trusted : FolderTrust.Untrusted;
    }

    /// <summary>Anything that can't even be opened for inspection counts as untrusted.</summary>
    public static FolderTrust Inspect(string path, bool directory = true)
    {
        try
        {
            using var handle = Open(path);
            return Inspect(handle, directory);
        }
        catch (Win32Exception)
        {
            return FolderTrust.Untrusted;
        }
    }

    /// <summary>
    /// The root and the work folder are real, admin-owned folders and the work folder holds the sentinel. Then the
    /// root only admins can change and a work folder that can't be emptied stay what they are while the app uses them.
    /// </summary>
    public static bool IsTrustedWorkFolder(string root, string work) =>
        Inspect(root) == FolderTrust.Trusted &&
        Inspect(work) == FolderTrust.Trusted &&
        Inspect(SentinelPath(work), directory: false) == FolderTrust.Trusted;

    static string OwnerOf(SafeFileHandle handle)
    {
        var rc = Native.GetSecurityInfo(handle, Native.SE_FILE_OBJECT, Native.OWNER_SECURITY_INFORMATION,
            out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (rc != 0)
            throw new Win32Exception((int)rc);
        try
        {
            return new SecurityIdentifier(owner).Value;
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }
}
