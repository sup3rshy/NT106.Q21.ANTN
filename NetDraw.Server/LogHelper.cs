using Newtonsoft.Json;

namespace NetDraw.Server;

internal static class LogHelper
{
    // Encode untrusted strings via JSON (escapes all C0 controls incl. ANSI ESC, CR/LF, unicode
    // bidi via \uXXXX, plus quotes). Stripping individual "dangerous" chars is unsafe; encoding
    // is the OWASP-recommended pattern for log forging defense.
    public static string SanitizeForLog(string? s, int maxLen = 200)
    {
        var encoded = JsonConvert.ToString(s ?? string.Empty);
        return encoded.Length > maxLen ? encoded[..maxLen] + "…\"" : encoded;
    }
}
