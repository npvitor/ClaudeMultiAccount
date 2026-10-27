using System;
using System.Windows;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Microsoft.Win32;

namespace ClaudeMultiAccount.UI;

internal static class Dialogs
{
    public static void Error(Window? owner, Exception ex)
    {
        var text = ex is LauncherException ? ex.Message : string.Format(S.ErrUnexpected, ex.Message);
        Show(owner, text, MessageBoxImage.Error);
    }

    public static void Info(Window? owner, string text) => Show(owner, text, MessageBoxImage.Information);

    public static bool Confirm(Window? owner, string text) =>
        Usable(owner)
            ? MessageBox.Show(owner!, text, S.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes
            : MessageBox.Show(text, S.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Asks for the Claude installer when the main account's MSIX package can't be reused.</summary>
    public static string? PickInstaller(Window? owner)
    {
        Info(owner, S.SelectInstallerInfo);
        var dialog = new OpenFileDialog { Title = S.SelectInstallerTitle, Filter = S.InstallerFilter, CheckFileExists = true };
        return dialog.ShowDialog(Usable(owner) ? owner : null) == true ? dialog.FileName : null;
    }

    static void Show(Window? owner, string text, MessageBoxImage image)
    {
        if (Usable(owner))
            MessageBox.Show(owner!, text, S.AppName, MessageBoxButton.OK, image);
        else
            MessageBox.Show(text, S.AppName, MessageBoxButton.OK, image);
    }

    // A hidden window (app minimized to the tray) can't own a dialog.
    static bool Usable(Window? owner) => owner is { IsVisible: true };
}
