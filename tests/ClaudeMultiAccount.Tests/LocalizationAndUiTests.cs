using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using ClaudeMultiAccount.UI;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class LocalizationTests
{
    static IEnumerable<PropertyInfo> Strings() =>
        typeof(S).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string));

    static string Get(PropertyInfo p, string culture)
    {
        using var _ = new CultureScope(culture);
        return (string)p.GetValue(null)!;
    }

    static int[] Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();

    public static TheoryData<string> Names() => new(Strings().Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public void Both_languages_have_text_and_the_same_placeholders(string name)
    {
        var p = typeof(S).GetProperty(name)!;
        var pt = Get(p, "pt-BR");
        var en = Get(p, "en-US");
        Assert.False(string.IsNullOrWhiteSpace(pt));
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.Equal(Placeholders(en), Placeholders(pt));

        // Must not throw on string.Format (stray braces, missing indexes).
        var args = Enumerable.Range(0, Placeholders(en).DefaultIfEmpty(-1).Max() + 1).Select(i => (object)("arg" + i)).ToArray();
        if (args.Length > 0)
        {
            string.Format(pt, args);
            string.Format(en, args);
        }
    }

    [Fact]
    public void Language_follows_the_windows_ui_culture()
    {
        Assert.Equal("Novo perfil", Get(typeof(S).GetProperty(nameof(S.NewProfile))!, "pt-PT"));
        Assert.Equal("New profile", Get(typeof(S).GetProperty(nameof(S.NewProfile))!, "en-GB"));
        Assert.Equal("New profile", Get(typeof(S).GetProperty(nameof(S.NewProfile))!, "de-DE"));
    }

    [Fact]
    public void Installer_filter_is_a_valid_file_dialog_filter()
    {
        foreach (var culture in new[] { "pt-BR", "en-US" })
        {
            var filter = Get(typeof(S).GetProperty(nameof(S.InstallerFilter))!, culture);
            var parts = filter.Split('|');
            Assert.Equal(2, parts.Length);
            Assert.Equal("*.exe;*.msix;*.msixbundle", parts[1]);
        }
    }
}

public class ProfileCardTests
{
    [Fact]
    public void Main_account_card()
    {
        var card = new ProfileCard(null);
        Assert.Null(card.Profile);
        Assert.Equal(S.MainName, card.Name);
        Assert.Contains(Environment.UserName, card.Detail);
        Assert.Equal(Visibility.Collapsed, card.MoreVisibility);
        Assert.Equal(S.MainName[..1].ToUpperInvariant(), card.Initial);
    }

    [Fact]
    public void Profile_card_shows_user_admin_and_color()
    {
        var profile = new Profile { DisplayName = "pessoal", WindowsUser = "claude-pessoal", Color = "#DC2626", IsAdmin = true };
        var card = new ProfileCard(profile);
        Assert.Same(profile, card.Profile);
        Assert.Equal("P", card.Initial);
        Assert.Contains("claude-pessoal", card.Detail);
        Assert.EndsWith(S.ProfileAdminSuffix, card.Detail);
        Assert.Equal(Visibility.Visible, card.MoreVisibility);
        Assert.Equal(Color.FromRgb(0xDC, 0x26, 0x26), ((SolidColorBrush)card.ColorBrush).Color);
    }

    [Fact]
    public void Empty_name_and_bad_color_do_not_crash()
    {
        var card = new ProfileCard(new Profile { DisplayName = "", Color = "not a color" });
        Assert.Equal("?", card.Initial);
        Assert.Same(Brushes.SlateGray, card.ColorBrush);
        Assert.Same(Brushes.SlateGray, ProfileCard.ParseBrush(""));
    }
}

public class StepItemTests
{
    [Fact]
    public void State_drives_glyph_spinner_and_detail()
    {
        var step = new StepItem("Create", "Approve UAC");
        var changed = new List<string?>();
        step.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("○", step.Glyph);
        Assert.Equal("", step.ElapsedText);
        Assert.Equal(Visibility.Collapsed, step.SpinnerVisibility);
        Assert.Equal(Visibility.Collapsed, step.DetailVisibility);

        step.State = StepState.Running;
        Assert.Equal("●", step.Glyph);
        Assert.Equal(Visibility.Visible, step.SpinnerVisibility);
        Assert.Equal(Visibility.Collapsed, step.GlyphVisibility);
        Assert.Equal(Visibility.Visible, step.DetailVisibility);
        Assert.Matches(@"^\d+:\d\d$", step.ElapsedText);
        Assert.Contains(nameof(StepItem.GlyphBrush), changed);

        changed.Clear();
        step.Tick();
        Assert.Equal(new[] { nameof(StepItem.ElapsedText) }, changed);

        step.State = StepState.Done;
        Assert.Equal("✓", step.Glyph);
        Assert.Equal(Visibility.Collapsed, step.DetailVisibility);
        changed.Clear();
        step.Tick(); // finished steps stop ticking
        Assert.Empty(changed);

        step.State = StepState.Failed;
        Assert.Equal("✕", step.Glyph);
    }

    [Fact]
    public void Step_without_detail_never_shows_it()
    {
        var step = new StepItem("Git") { State = StepState.Running };
        Assert.Equal(Visibility.Collapsed, step.DetailVisibility);
    }
}
