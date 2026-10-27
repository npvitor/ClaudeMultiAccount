using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeMultiAccount.Core;

internal static class Native
{
    // advapi32 / kernel32: start a process as another local user
    public const uint LOGON_WITH_PROFILE = 0x1;
    public const uint CREATE_NEW_CONSOLE = 0x10;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const int STARTF_USESHOWWINDOW = 0x1;
    public const short SW_HIDE = 0;
    public const short SW_SHOWNORMAL = 1;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_TIMEOUT = 0x102;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessWithLogonW(
        string lpUsername, string? lpDomain, string lpPassword, uint dwLogonFlags,
        string? lpApplicationName, StringBuilder lpCommandLine, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    // netapi32: local user management (elevated only)
    public const uint USER_PRIV_USER = 1;
    public const uint UF_SCRIPT = 0x1;
    public const uint UF_PASSWD_CANT_CHANGE = 0x40;
    public const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
    public const int NERR_UserNotFound = 2221;
    public const int NERR_UserExists = 2224;
    public const int NERR_PasswordTooShort = 2245;
    public const int ERROR_MEMBER_NOT_IN_ALIAS = 1377;
    public const int ERROR_MEMBER_IN_ALIAS = 1378;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        public string? usri1_home_dir;
        public string? usri1_comment;
        public uint usri1_flags;
        public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1011
    {
        public string usri1011_full_name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOCALGROUP_MEMBERS_INFO_3
    {
        public string lgrmi3_domainandname;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetUserAdd(string? servername, uint level, ref USER_INFO_1 buf, out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetUserSetInfo(string? servername, string username, uint level, ref USER_INFO_1011 buf, out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetUserDel(string? servername, string username);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetLocalGroupAddMembers(string? servername, string groupname, uint level, ref LOCALGROUP_MEMBERS_INFO_3 buf, uint totalentries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetLocalGroupDelMembers(string? servername, string groupname, uint level, ref LOCALGROUP_MEMBERS_INFO_3 buf, uint totalentries);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteProfileW")]
    public static extern bool DeleteProfile(string lpSidString, string? lpProfilePath, string? lpComputerName);

    // crypt32: DPAPI, so the generated passwords are only readable by the current Windows user
    public const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    // kernel32 / advapi32: look at a file or folder itself, never at what a junction or link points to
    public const uint READ_CONTROL = 0x20000;
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const int SE_FILE_OBJECT = 1;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public uint ftCreationLow, ftCreationHigh, ftLastAccessLow, ftLastAccessHigh, ftLastWriteLow, ftLastWriteHigh;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("advapi32.dll")]
    public static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, out IntPtr ppsidOwner,
        IntPtr ppsidGroup, IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);
}
