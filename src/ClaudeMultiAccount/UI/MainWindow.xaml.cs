using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;

namespace ClaudeMultiAccount.UI;

/// <summary>One row in the main window. <see cref="Profile"/> is null for the main Windows account.</summary>
public sealed class ProfileCard
{
    const string MainColor = "#57534E";

    public ProfileCard(Profile? profile)
    {
        Profile = profile;
        Name = profile?.DisplayName ?? S.MainName;
        Detail = profile is null
            ? string.Format(S.MainDetail, Environment.UserName)
            : string.Format(S.ProfileDetail, profile.WindowsUser) + (profile.IsAdmin ? S.ProfileAdminSuffix : "");
        Initial = Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
        ColorBrush = ParseBrush(profile?.Color ?? MainColor);
        MoreVisibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public Profile? Profile { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Initial { get; }
    public Brush ColorBrush { get; }
    public Visibility MoreVisibility { get; }

    public static Brush ParseBrush(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return Brushes.SlateGray; }
    }
}

public partial class MainWindow : Window
{
    // Claude updates are rare; re-detecting (a PowerShell run) on every click isn't worth the delay.
    static readonly TimeSpan InstallCacheTime = TimeSpan.FromMinutes(10);

    readonly ProfileStore _store;
    // One operation at a time; extra requests (e.g. from the tray) wait their turn instead of being dropped.
    readonly SemaphoreSlim _queue = new(1, 1);
    ClaudeInstall? _install;
    DateTime _installCheckedAt;
    bool _exiting, _trayHintShown;

    public MainWindow(ProfileStore store)
    {
        _store = store;
        InitializeComponent();
        Refresh();
        Loaded += async (_, _) => await DetectAsync(force: true);
    }

    App? CurrentApp => Application.Current as App;

    void Refresh()
    {
        var cards = new List<ProfileCard> { new(null) };
        cards.AddRange(_store.Profiles.Select(p => new ProfileCard(p)));
        ProfileList.ItemsSource = cards;
        CurrentApp?.Tray?.Rebuild();
    }

    /// <summary>Cached for a few minutes; "not installed" is always re-checked so a fresh install shows up at once.</summary>
    async Task<ClaudeInstall> DetectAsync(bool force = false)
    {
        if (!force && _install is { Kind: not InstallKind.None } && DateTime.UtcNow - _installCheckedAt < InstallCacheTime)
            return _install;
        _install = await Task.Run(ClaudeDesktop.Detect);
        _installCheckedAt = DateTime.UtcNow;
        Banner.Visibility = _install.Kind == InstallKind.None ? Visibility.Visible : Visibility.Collapsed;
        return _install;
    }

    /// <summary>Opens Claude for a profile (or the main account when null). Also used by the tray menu.</summary>
    public Task LaunchAsync(Profile? profile) =>
        RunBusyAsync(profile is null ? S.StatusOpeningMain : string.Format(S.StatusOpening, profile.DisplayName), async progress =>
        {
            var install = await DetectAsync();
            if (profile is null)
            {
                ClaudeDesktop.LaunchCurrentUser(install);
                return;
            }
            var password = Secret.Unprotect(profile.ProtectedPassword);
            await ProfileSetup.EnsureUpToDateAsync(profile, password, install, _store, progress);
            await Task.Run(() => ClaudeDesktop.LaunchForProfile(profile, password));
        });

    async Task RunBusyAsync(string status, Func<IProgress<string>, Task> work)
    {
        await _queue.WaitAsync();
        ContentArea.IsEnabled = false;
        StatusText.Text = status;
        StatusBar.Visibility = Visibility.Visible;
        try
        {
            await work(new Progress<string>(text => StatusText.Text = text));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex);
        }
        finally
        {
            ContentArea.IsEnabled = true;
            StatusBar.Visibility = Visibility.Collapsed;
            _queue.Release();
        }
    }

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileCard card })
            await LaunchAsync(card.Profile);
    }

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProfileWindow(_store, null) { Owner = this };
        var created = window.ShowDialog() == true;
        Refresh();
        if (created && window.LaunchAfter && window.Result is { } profile)
            await LaunchAsync(profile);
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileCard { Profile: { } profile } } button)
            return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuItem(S.MenuTerminal, () => _ = OpenTerminalAsync(profile)));
        menu.Items.Add(MenuItem(S.MenuEdit, () => Edit(profile)));
        menu.Items.Add(MenuItem(S.MenuRepair, () => _ = RepairAsync(profile)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(S.MenuCopyPassword, () => CopyPassword(profile)));
        menu.Items.Add(MenuItem(S.MenuRemove, () => _ = RemoveAsync(profile)));
        menu.IsOpen = true;
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var startup = new MenuItem { Header = S.StartWithWindows, IsCheckable = true, IsChecked = StartupRegistration.IsEnabled };
        startup.Click += (_, _) =>
        {
            try { StartupRegistration.Set(startup.IsChecked); }
            catch (Exception ex) { Dialogs.Error(this, ex); }
        };
        menu.Items.Add(startup);
        menu.Items.Add(MenuItem(S.OpenLogs, () =>
        {
            if (Directory.Exists(Paths.Shared))
                Process.Start(new ProcessStartInfo(Paths.Shared) { UseShellExecute = true });
            else
                Dialogs.Info(this, S.NoLogsYet);
        }));
        menu.Items.Add(MenuItem(S.About, () =>
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
            Dialogs.Info(this, string.Format(S.AboutText, version));
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(S.Exit, () => CurrentApp?.ExitApp()));
        menu.IsOpen = true;
    }

    void Download_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://claude.ai/download") { UseShellExecute = true });

    Task OpenTerminalAsync(Profile profile) =>
        RunBusyAsync(string.Format(S.StatusOpeningTerminal, profile.DisplayName), _ =>
        {
            var password = Secret.Unprotect(profile.ProtectedPassword);
            return Task.Run(() => ClaudeDesktop.OpenTerminal(profile, password));
        });

    void Edit(Profile profile)
    {
        var window = new ProfileWindow(_store, profile) { Owner = this };
        window.ShowDialog();
        Refresh();
    }

    async Task RepairAsync(Profile profile)
    {
        var install = await DetectAsync(force: true);
        string? installer = null;
        if (install.Kind != InstallKind.Msix && (installer = Dialogs.PickInstaller(this)) is null)
            return;

        await RunBusyAsync(string.Format(S.StatusRepairing, profile.DisplayName), async _ =>
        {
            // The locked-down work folder is needed by every setup script; recreate it if it's gone.
            if (!Directory.Exists(Paths.ProfileWork(profile.WindowsUser)) &&
                await Elevation.RunAsync(new ElevatedRequest { Op = ElevatedOp.Secure, UserName = profile.WindowsUser }) is null)
                return;

            var password = Secret.Unprotect(profile.ProtectedPassword);
            await Task.Run(() => ProfileSetup.PrepareWindowsProfile(profile, password));
            await ProfileSetup.RegisterClaudeAsync(profile, password, install, installer, _store);
            await Task.Run(() => ProfileSetup.ConfigureGit(profile, password));
            Dialogs.Info(this, S.RepairDone);
        });
    }

    void CopyPassword(Profile profile)
    {
        try
        {
            Clipboard.SetText(Secret.Unprotect(profile.ProtectedPassword));
            Dialogs.Info(this, S.PasswordCopied);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex);
        }
    }

    async Task RemoveAsync(Profile profile)
    {
        if (!Dialogs.Confirm(this, string.Format(S.ConfirmRemove, profile.DisplayName, profile.WindowsUser)))
            return;

        await RunBusyAsync(string.Format(S.StatusRemoving, profile.DisplayName), async _ =>
        {
            var result = await Elevation.RunAsync(new ElevatedRequest
            {
                Op = ElevatedOp.Delete,
                UserName = profile.WindowsUser,
                Folders = profile.SharedFolders,
            });
            if (result is null)
                return;
            _store.Remove(profile);
            _store.Save();
            Refresh();
            if (result.Warning is { } warning)
                Dialogs.Info(this, warning);
        });
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps the app in the tray; "Exit" really quits.
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                CurrentApp?.Tray?.Balloon(S.ClosedToTray);
            }
        }
        base.OnClosing(e);
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }
}
