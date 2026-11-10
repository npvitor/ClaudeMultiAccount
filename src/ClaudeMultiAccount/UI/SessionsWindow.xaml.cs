using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Microsoft.Win32;

namespace ClaudeMultiAccount.UI;

public sealed class SessionItem : INotifyPropertyChanged
{
    bool _isChecked;

    public SessionItem(SessionInfo info)
    {
        Info = info;
        Title = info.Title.Length > 0 ? info.Title : S.SessionsUntitled;
        var parts = new List<string>();
        if (info.Cwd is { Length: > 0 } cwd)
            parts.Add(cwd);
        if (info.ModifiedUtc > DateTime.MinValue)
            parts.Add(info.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        parts.Add(FormatSize(info.Size));
        Detail = string.Join("  ·  ", parts);
    }

    public SessionInfo Info { get; }
    public string Title { get; }
    public string Detail { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
    };
}

public sealed record CopyTarget(ProfileCard Card, string Name);

/// <summary>Lists one account's Claude Code sessions, to save them to a file or copy them to another account.</summary>
public partial class SessionsWindow : Window
{
    readonly ProfileCard _account;
    List<SessionItem> _items = new();
    bool _busy;

    public SessionsWindow(ProfileCard account, IReadOnlyList<ProfileCard> others)
    {
        _account = account;
        InitializeComponent();
        Title = Heading.Text = string.Format(S.SessionsTitle, account.Name);
        // The account itself is a target too: after switching Claude logins on the same Windows user, the
        // conversations are already here and only need to show in the new login's sidebar.
        TargetBox.ItemsSource = others.Select(o => new CopyTarget(o, o.Name))
            .Append(new CopyTarget(account, string.Format(S.SessionsThisClaude, account.Name))).ToList();
        TargetBox.SelectedIndex = 0;
        Loaded += async (_, _) => await LoadAsync();
    }

    IReadOnlyList<SessionInfo> Selected => _items.Where(i => i.IsChecked).Select(i => i.Info).ToList();

    async Task LoadAsync()
    {
        SetBusy(S.SessionsLoading);
        try
        {
            var profile = _account.Profile;
            var sessions = await Task.Run(() => SessionTransfer.List(SessionHost.For(profile)));
            _items = sessions.Select(s => new SessionItem(s)).ToList();
            SessionList.ItemsSource = _items;
            EmptyText.Text = S.SessionsNone;
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex is LauncherException ? ex.Message : string.Format(S.ErrUnexpected, ex.Message);
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(null);
        }
    }

    void SetBusy(string? status)
    {
        var busy = _busy = status is not null;
        CloseButton.IsEnabled = !busy;
        StatusText.Text = status ?? "";
        StatusPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SessionList.IsEnabled = SelectAll.IsEnabled = !busy;
        UpdateCount(busy);
    }

    void UpdateCount(bool busy = false)
    {
        var selected = _items.Count(i => i.IsChecked);
        CountText.Text = _items.Count == 0 ? "" : string.Format(S.SessionsSelected, selected, _items.Count);
        SelectAll.IsChecked = _items.Count > 0 && selected == _items.Count ? true : selected == 0 ? false : null;
        SaveButton.IsEnabled = CopyButton.IsEnabled = TargetBox.IsEnabled = !busy && selected > 0;
    }

    void Item_Click(object sender, RoutedEventArgs e) => UpdateCount();

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var check = _items.Any(i => !i.IsChecked);
        foreach (var item in _items)
            item.IsChecked = check;
        UpdateCount();
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var sessions = Selected;
        var dialog = new SaveFileDialog
        {
            Filter = S.SessionsFilter,
            FileName = $"{S.SessionsFilePrefix}-{Slug(_account.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var path = dialog.FileName;
        var profile = _account.Profile;
        var name = _account.Name;
        await RunAsync(string.Format(S.StatusExporting, sessions.Count), async () =>
        {
            await Task.Run(() => SessionTransfer.Export(SessionHost.For(profile), sessions, name, path));
            Dialogs.Info(this, string.Format(S.ExportDone, sessions.Count, path));
        });
    }

    async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (TargetBox.SelectedItem is not CopyTarget { Card: var target })
            return;
        var sessions = Selected;
        var from = _account.Profile;
        var name = _account.Name;
        await RunAsync(string.Format(S.StatusCopying, sessions.Count, target.Name), async () =>
        {
            var result = await Task.Run(() =>
                SessionTransfer.Copy(SessionHost.For(from), sessions, name, SessionHost.For(target.Profile)));
            Dialogs.Info(this, DescribeImport(result, target));
        });
    }

    async Task RunAsync(string status, Func<Task> work)
    {
        SetBusy(status);
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex);
        }
        finally
        {
            SetBusy(null);
        }
    }

    /// <summary>The import summary: what happened to each session, folders the profile can't reach, how to continue.</summary>
    internal static string DescribeImport(ImportResult result, ProfileCard target)
    {
        var text = new StringBuilder(string.Format(S.ImportSummary, target.Name)).AppendLine();
        void Line(ImportState state, string format)
        {
            var count = result.Count(state);
            if (count > 0)
                text.AppendLine("•  " + string.Format(format, count));
        }
        Line(ImportState.New, S.ImportNew);
        Line(ImportState.Updated, S.ImportUpdated);
        Line(ImportState.Unchanged, S.ImportUnchanged);
        Line(ImportState.KeptNewer, S.ImportKeptNewer);
        Line(ImportState.Conflict, S.ImportConflict);

        if (target.Profile is { } profile)
        {
            var missing = result.WrittenFolders.Where(f => !profile.SharedFolders.Any(shared => IsUnder(f, shared))).ToList();
            if (missing.Count > 0)
                text.AppendLine().AppendLine(string.Format(S.ImportFoldersMissing, string.Join("\n", missing.Select(m => "   " + m))));
        }
        if (result.Count(DesktopListing.Added) > 0)
            text.AppendLine().AppendLine(string.Format(S.ImportListed, result.Count(DesktopListing.Added)));
        else if (result.Desktop == DesktopStatus.NoAccount)
            text.AppendLine().AppendLine(S.ImportDesktopNoAccount);
        if (result.Count(DesktopListing.Archived) > 0)
            text.AppendLine().AppendLine(string.Format(S.ImportArchived, result.Count(DesktopListing.Archived)));
        if (result.Count(ImportState.New) + result.Count(ImportState.Updated) > 0)
            text.AppendLine().Append(S.ImportHowToResume);
        return text.ToString().TrimEnd();
    }

    internal static bool IsUnder(string path, string folder)
    {
        var p = path.TrimEnd('\\', '/');
        var f = folder.TrimEnd('\\', '/');
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static string Slug(string name) => LocalUsers.Slug(name) is { Length: > 0 } slug ? slug : "account";

    protected override void OnClosing(CancelEventArgs e)
    {
        // The copy goes on in the background and would race with what the user does next; wait for it.
        e.Cancel = _busy;
        base.OnClosing(e);
    }
}
