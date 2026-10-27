using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeMultiAccount.Core;
using ClaudeMultiAccount.Localization;
using Xunit;

namespace ClaudeMultiAccount.Tests;

public class SecretTests
{
    const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    const string Lower = "abcdefghijkmnopqrstuvwxyz";
    const string Digits = "23456789";
    const string Symbols = "!#$%*+-=?@_";

    [Theory]
    [InlineData("simple")]
    [InlineData("")]
    [InlineData("João Ção ✓ 日本語 !#$%*+-=?@_")]
    public void Protect_then_Unprotect_round_trips(string plain)
    {
        var protectedText = Secret.Protect(plain);
        Assert.NotEqual(plain, protectedText);
        Assert.Equal(plain, Secret.Unprotect(protectedText));
    }

    [Fact]
    public void Protect_output_is_base64_and_not_deterministic()
    {
        var a = Secret.Protect("same");
        var b = Secret.Protect("same");
        Convert.FromBase64String(a); // must not throw
        Assert.NotEqual(a, b); // DPAPI salts every blob
    }

    [Fact]
    public void Unprotect_of_tampered_data_gives_friendly_error()
    {
        var bytes = Convert.FromBase64String(Secret.Protect("secret"));
        bytes[^5] ^= 0xFF;
        var ex = Assert.Throws<LauncherException>(() => Secret.Unprotect(Convert.ToBase64String(bytes)));
        Assert.Equal(S.ErrPasswordUnreadable, ex.Message);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("")]
    public void Unprotect_of_garbage_gives_friendly_error(string garbage)
    {
        var ex = Assert.Throws<LauncherException>(() => Secret.Unprotect(garbage));
        Assert.Equal(S.ErrPasswordUnreadable, ex.Message);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(4)]
    [InlineData(64)]
    public void GeneratePassword_has_requested_length_and_every_class(int length)
    {
        for (int i = 0; i < 200; i++)
        {
            var pw = Secret.GeneratePassword(length);
            Assert.Equal(length, pw.Length);
            Assert.Contains(pw, Upper.Contains);
            Assert.Contains(pw, Lower.Contains);
            Assert.Contains(pw, Digits.Contains);
            Assert.Contains(pw, Symbols.Contains);
            Assert.All(pw, c => Assert.Contains(c, Upper + Lower + Digits + Symbols));
        }
    }

    [Fact]
    public void GeneratePassword_default_is_28_and_unique()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
            Assert.True(seen.Add(Secret.GeneratePassword()));
        Assert.All(seen, p => Assert.Equal(28, p.Length));
    }

    [Fact]
    public void GeneratePassword_never_uses_ambiguous_or_shell_breaking_characters()
    {
        var all = string.Concat(Enumerable.Range(0, 500).Select(_ => Secret.GeneratePassword()));
        foreach (var bad in "0O1lI\"'`^&|<> ")
            Assert.DoesNotContain(bad, all);
    }
}
