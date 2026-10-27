using System;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Forms = System.Windows.Forms;

namespace ClaudeMultiAccount.UI;

/// <summary>Notification-area icon with a quick "open profile" menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly ProfileStore _store;
    readonly Action _show;
    readonly Action<Profile?> _launch;
    readonly Action _exit;

    public TrayIcon(ProfileStore store, Action show, Action<Profile?> launch, Action exit)
    {
        _store = store;
        _show = show;
        _launch = launch;
        _exit = exit;
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = S.AppName,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _show();
        Rebuild();
    }

    public void Rebuild()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add($"{S.OpenClaude} — {S.MainName}", null, (_, _) => _launch(null));
        foreach (var profile in _store.Profiles)
        {
            var p = profile;
            menu.Items.Add($"{S.OpenClaude} — {p.DisplayName}", null, (_, _) => _launch(p));
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(S.ShowWindow, null, (_, _) => _show());
        menu.Items.Add(S.Exit, null, (_, _) => _exit());

        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void Balloon(string text) => _icon.ShowBalloonTip(4000, S.AppName, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
