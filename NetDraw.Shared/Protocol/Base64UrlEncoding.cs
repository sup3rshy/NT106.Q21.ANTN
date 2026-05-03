using System;

namespace NetDraw.Shared.Protocol;

public static class Base64UrlEncoding
{
    public static string Encode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // Returns null on empty / whitespace / out-of-alphabet / bad padding.
    // Missing-vs-malformed deliberately indistinguishable to callers — see session-token.md.
    public static byte[]? TryDecode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        foreach (var ch in input)
        {
            if (!IsBase64UrlChar(ch)) return null;
        }

        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 0: break;
            default: return null;
        }

        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsBase64UrlChar(char c) =>
        (c >= 'A' && c <= 'Z')
        || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9')
        || c == '-'
        || c == '_';
}
