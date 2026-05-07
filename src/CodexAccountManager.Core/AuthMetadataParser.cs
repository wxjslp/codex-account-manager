using System.Text;
using System.Text.Json;

namespace CodexAccountManager.Core;

public static class AuthMetadataParser
{
    public static AuthMetadata FromFile(string authPath)
    {
        using var stream = File.OpenRead(authPath);
        using var document = JsonDocument.Parse(stream);
        return FromJson(document.RootElement);
    }

    public static AuthMetadata FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("auth.json 必须是 JSON 对象。");
        }

        var authMode = ReadString(root, "auth_mode")
            ?? (root.TryGetProperty("OPENAI_API_KEY", out _) ? "api_key" : null)
            ?? "unknown";

        root.TryGetProperty("tokens", out var tokens);
        if (tokens.ValueKind != JsonValueKind.Object)
        {
            tokens = root;
        }

        var idToken = ReadString(tokens, "id_token");
        var accountId = ReadString(tokens, "account_id");
        var lastRefreshAt = ReadLastRefresh(tokens);
        var claims = DecodeJwtPayload(idToken);
        var authClaims = ClaimsObject(claims, "https://api.openai.com/auth");

        var email = ClaimString(claims, "email");
        var displayName =
            ClaimString(claims, "name")
            ?? email
            ?? accountId
            ?? "未命名账号";
        var planType = ClaimString(authClaims, "chatgpt_plan_type");
        accountId ??= ClaimString(authClaims, "chatgpt_account_id");

        if (!root.TryGetProperty("tokens", out _) && root.TryGetProperty("OPENAI_API_KEY", out _))
        {
            displayName = displayName == "未命名账号" ? "API Key 账号" : displayName;
        }

        if (!root.TryGetProperty("tokens", out _) && !root.TryGetProperty("OPENAI_API_KEY", out _) && authMode == "unknown")
        {
            throw new InvalidDataException("auth.json 缺少可识别的认证字段。");
        }

        return new AuthMetadata(accountId, displayName, email, planType, authMode, lastRefreshAt);
    }

    public static Dictionary<string, object?> DecodeJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);
            return ToDictionary(document.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, object?> ClaimsObject(Dictionary<string, object?> claims, string key)
    {
        if (claims.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested)
        {
            return nested;
        }

        return [];
    }

    private static string? ClaimString(Dictionary<string, object?> claims, string key)
    {
        return claims.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static DateTimeOffset? ReadLastRefresh(JsonElement tokens)
    {
        if (tokens.ValueKind != JsonValueKind.Object || !tokens.TryGetProperty("last_refresh", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Object => ToDictionary(property.Value),
                JsonValueKind.Array => property.Value.GetRawText(),
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when property.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        return result;
    }
}
