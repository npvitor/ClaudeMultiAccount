using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Microsoft.Win32;

namespace ClaudeMultiAccount.UI;

public enum StepState { Pending, Running, Done, Failed }

public sealed class StepItem : INotifyPropertyChanged
{
    readonly Stopwatch _clock = new();
    StepState _state;

    public StepItem(string text, string? detail = null)
    {
        Text = text;
        Detail = detail;
    }

    public string Text { get; }
    /// <summary>What is happening right now, shown under the step while it runs.</summary>
    public string? Detail { get; }

    public StepState State
    {
        get => _state;
        set
        {
            _state = value;
            if (value == StepState.Running)
                _clock.Restart();
            else
                _clock.Stop();
            foreach (var name in new[] { nameof(Glyph), nameof(GlyphBrush), nameof(TextBrush), nameof(GlyphVisibility),
                                         nameof(SpinnerVisibility), nameof(DetailVisibility), nameof(ElapsedText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public Visibility GlyphVisibility => State == StepState.Running ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SpinnerVisibility => State == StepState.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => State == StepState.Running && Detail is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Time spent on the step ("0:42"), so a slow step visibly keeps moving.</summary>
    public string ElapsedText => State == StepState.Pending ? "" : $"{(int)_clock.Elapsed.TotalMinutes}:{_clock.Elapsed.Seconds:00}";

    /// <summary>Called every second by the window to refresh <see cref="ElapsedText"/>.</summary>
    public void Tick()
    {
        if (State == StepState.Running)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedText)));
    }

    public string Glyph => State switch
    {
        StepState.Running => "●",
        StepState.Done => "✓",
        StepState.Failed => "✕",
        _ => "○",
    };

    public Brush GlyphBrush => State switch
    {
        StepState.Running => Res("Accent"),
        StepState.Done => Res("Success"),
        StepState.Failed => Res("Danger"),
        _ => Res("Muted"),
    };

    public Brush TextBrush => State == StepState.Pending ? Res("Muted") : Res("Ink");

    public event PropertyChangedEventHandler? PropertyChanged;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>Creates a new profile (with a step-by-step progress view) or edits an existing one.</summary>
public partial class ProfileWindow : Window
{
    static readonly string[] Palette =
        { "#4F46E5", "#0284C7", "#059669", "#D97706", "#DC2626", "#DB2777", "#7C3AED", "#475569" };

    readonly ProfileStore _store;
    readonly Profile? _editing;
    readonly ObservableCollection<string> _folders = new();
    string _color;
    bool _running;

    public Profile? Result { get; private set; }
    public bool LaunchAfter { get; private set; }

    public ProfileWindow(ProfileStore store, Profile? editing)
    {
        _store = store;
        _editing = editing;
        InitializeComponent();

        Title = Heading.Text = editing is null ? S.NewProfileTitle : S.EditProfileTitle;
        SaveButton.Content = editing is null ? S.CreateProfile : S.Save;
        AdminNotice.Text = editing is null ? S.AdminNoticeCreate : S.AdminNoticeEdit;
        LaunchCheck.Visibility = editing is null ? Visibility.Visible : Visibility.Collapsed;
        FolderList.ItemsSource = _folders;

        if (editing is not null)
        {
            NameBox.Text = editing.DisplayName;
            foreach (var folder in editing.SharedFolders)
                _folders.Add(folder);
            GitNameBox.Text = editing.GitName ?? "";
            GitEmailBox.Text = editing.GitEmail ?? "";
            _color = editing.Color;
        }
        else
        {
            _color = Palette[store.Profiles.Count % Palette.Length];
            foreach (var folder in DefaultFolders())
                _folders.Add(folder);
        }

        BuildColorChips();
        UpdateUserHint();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Common places where people keep repositories, so most users don't need to add anything.</summary>
    static string[] DefaultFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(profile, "source", "repos"), // Visual Studio default
            @"C:\Git", @"C:\src", @"C:\Projects", @"C:\dev",
        }.Where(Directory.Exists).ToArray();
    }

    void BuildColorChips()
    {
        foreach (var hex in Palette.Contains(_color) ? Palette : Palette.Append(_color))
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ColorChip"),
                Background = ProfileCard.ParseBrush(hex),
                GroupName = "ProfileColor",
                IsChecked = string.Equals(hex, _color, StringComparison.OrdinalIgnoreCase),
                Tag = hex,
            };
            chip.Checked += (_, _) => _color = hex;
            ColorPanel.Children.Add(chip);
        }
    }

    void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateUserHint();

    void UpdateUserHint()
    {
        if (_editing is not null)
        {
            UserHint.Text = string.Format(S.WindowsUserHint, _editing.WindowsUser);
            return;
        }
        var name = NameBox.Text.Trim();
        UserHint.Text = name.Length == 0
            ? S.NamePlaceholderHint
            : string.Format(S.WindowsUserHint, LocalUsers.SuggestUserName(name, _store.Profiles));
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = S.AddFolder };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (var folder in dialog.FolderNames)
            if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _folders.Add(folder);
    }

    void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in FolderList.SelectedItems.Cast<string>().ToList())
            _folders.Remove(folder);
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Dialogs.Info(this, S.ErrNameRequired);
            return;
        }
        if (name.Contains('"'))
        {
            Dialogs.Info(this, S.ErrNameInvalid);
            return;
        }
        if (_folders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing)
        {
            Dialogs.Info(this, string.Format(S.ErrFolderMissing, missing));
            return;
        }

        if (_editing is null)
            await CreateAsync(name);
        else
            await SaveEditAsync(_editing, name);
    }

    async Task CreateAsync(string name)
    {
        FormPanel.IsEnabled = false;
        var install = await Task.Run(ClaudeDesktop.Detect);
        FormPanel.IsEnabled = true;

        // Ask for the installer up front, before anything is created on the system.
        string? installer = null;
        if (install.Kind != InstallKind.Msix && (installer = Dialogs.PickInstaller(this)) is null)
            return;

        var password = Secret.GeneratePassword();
        var profile = new Profile
        {
            DisplayName = name,
            WindowsUser = LocalUsers.SuggestUserName(name, _store.Profiles),
            Color = _color,
            SharedFolders = _folders.ToList(),
            GitName = Clean(GitNameBox.Text),
            GitEmail = Clean(GitEmailBox.Text),
            ProtectedPassword = Secret.Protect(password),
        };

        var steps = new[]
        {
            new StepItem(S.StepCreateUser, S.StepCreateUserDetail),
            new StepItem(S.StepPrepare, S.StepPrepareDetail),
            new StepItem(S.StepRegister, S.StepRegisterDetail),
            new StepItem(S.StepGit),
        };
        StepList.ItemsSource = steps;
        FormPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        WorkingBar.Visibility = Visibility.Visible;
        _running = true;

        // Keeps the elapsed time ticking so long steps visibly make progress.
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
        {
            foreach (var step in steps)
                step.Tick();
        };
        clock.Start();

        try
        {
            await RunStep(steps[0], async () =>
            {
                var created = await Elevation.RunAsync(new ElevatedRequest
                {
                    Op = ElevatedOp.Create,
                    UserName = profile.WindowsUser,
                    FullName = $"Claude - {name}",
                    ProtectedPassword = profile.ProtectedPassword,
                    Folders = profile.SharedFolders,
                });
                if (created is null)
                    throw new OperationCanceledException();
                // Save right away so a later failure can still be repaired or removed from the main window.
                _store.Add(profile);
                _store.Save();
                Result = profile;
            });
            await RunStep(steps[1], () => Task.Run(() => ProfileSetup.PrepareWindowsProfile(profile, password)));
            await RunStep(steps[2], () => ProfileSetup.RegisterClaudeAsync(profile, password, install, installer, _store));
            await RunStep(steps[3], () => Task.Run(() => ProfileSetup.ConfigureGit(profile, password)));

            _running = false;
            LaunchAfter = LaunchCheck.IsChecked == true;
            await Task.Delay(600);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (Result is null)
        {
            // UAC prompt dismissed: nothing was created, go back to the form.
            _running = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            FormPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _running = false;
            var message = ex is LauncherException ? ex.Message : string.Format(S.ErrUnexpected, ex.Message);
            ErrorText.Text = Result is null ? message : message + "\n\n" + S.ErrPartial;
            ErrorBox.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Visible;
        }
        finally
        {
            clock.Stop();
            WorkingBar.Visibility = Visibility.Collapsed;
        }
    }

    static async Task RunStep(StepItem step, Func<Task> work)
    {
        step.State = StepState.Running;
        try
        {
            await work();
            step.State = StepState.Done;
        }
        catch
        {
            step.State = StepState.Failed;
            throw;
        }
    }

    async Task SaveEditAsync(Profile profile, string name)
    {
        var folders = _folders.ToList();
        var added = folders.Where(f => !profile.SharedFolders.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var removed = profile.SharedFolders.Where(f => !folders.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

        FormPanel.IsEnabled = false;
        SaveButton.Content = S.Saving;
        _running = true;
        try
        {
            if (added.Count > 0 || removed.Count > 0)
            {
                var changed = await Elevation.RunAsync(new ElevatedRequest
                {
                    Op = ElevatedOp.Folders,
                    UserName = profile.WindowsUser,
                    Folders = added,
                    RevokeFolders = removed,
                });
                if (changed is null)
                    return;
            }

            profile.DisplayName = name;
            profile.Color = _color;
            profile.SharedFolders = folders;
            profile.GitName = Clean(GitNameBox.Text);
            profile.GitEmail = Clean(GitEmailBox.Text);
            _store.Save();

            var password = Secret.Unprotect(profile.ProtectedPassword);
            await Task.Run(() => ProfileSetup.ConfigureGit(profile, password));

            Result = profile;
            _running = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex);
        }
        finally
        {
            _running = false;
            FormPanel.IsEnabled = true;
            SaveButton.Content = S.Save;
        }
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = Result is not null;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Don't leave a half-created profile behind by closing mid-setup.
        if (_running)
            e.Cancel = true;
        base.OnClosing(e);
    }

    static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
