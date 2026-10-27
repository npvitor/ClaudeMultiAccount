using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeMultiAccount.Core;

/// <summary>Generates profile passwords and stores them with DPAPI (current Windows user only).</summary>
public static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeMultiAccount/v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), protect: true));

    public static string Unprotect(string protectedBase64) =>
        Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedBase64), protect: false));

    public static string GeneratePassword(int length = 28)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!#$%*+-=?@_";
        const string all = upper + lower + digits + symbols;

        var chars = new char[length];
        // Guarantee every class so any local password policy is satisfied.
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (int i = 4; i < length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new Native.DATA_BLOB { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entropyBlob = new Native.DATA_BLOB { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            Native.DATA_BLOB outBlob;
            bool ok = protect
                ? Native.CryptProtectData(ref inBlob, "ClaudeMultiAccount", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, Native.CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                : Native.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, Native.CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                Native.LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}
