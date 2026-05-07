using System.Text.RegularExpressions;

namespace CodexAccountManager.Core;

public static partial class SecretRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = JwtPattern().Replace(value, "[redacted-jwt]");
        redacted = TokenFieldPattern().Replace(redacted, "$1\"[redacted]\"");
        redacted = ApiKeyPattern().Replace(redacted, "sk-[redacted]");
        return redacted;
    }

    [GeneratedRegex(@"eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(""(?:access_token|refresh_token|id_token|api_key|OPENAI_API_KEY)""\s*:\s*)"".*?""", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TokenFieldPattern();

    [GeneratedRegex(@"sk-[a-zA-Z0-9_\-]{16,}", RegexOptions.Compiled)]
    private static partial Regex ApiKeyPattern();
}
