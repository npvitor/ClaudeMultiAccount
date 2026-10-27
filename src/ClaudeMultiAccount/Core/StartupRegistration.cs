using System;
using Microsoft.Win32;

namespace ClaudeMultiAccount.Core;

/// <summary>"Start with Windows": starts minimized to the tray.</summary>
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeMultiAccount";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
