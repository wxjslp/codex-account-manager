using System.Text;
using System.Text.Json;
using CodexAccountManager.Core;

namespace CodexAccountManager.Core.Tests;

public sealed class AuthMetadataParserTests
{
    [Fact]
    public void ParsesChatGptClaimsWithoutExposingTokens()
    {
        var idToken = CreateJwt("""
        {
          "email": "eli@example.test",
          "name": "Eli",
          "https://api.openai.com/auth": {
            "chatgpt_account_id": "acct_123",
            "chatgpt_plan_type": "plus"
          }
        }
        """);

        using var document = JsonDocument.Parse($$"""
        {
          "auth_mode": "chatgpt",
          "tokens": {
            "id_token": "{{idToken}}",
            "access_token": "secret-access",
            "refresh_token": "secret-refresh",
            "last_refresh": 1710000000
          }
        }
        """);

        var metadata = AuthMetadataParser.FromJson(document.RootElement);

        Assert.Equal("acct_123", metadata.AccountId);
        Assert.Equal("Eli", metadata.DisplayName);
        Assert.Equal("eli@example.test", metadata.Email);
        Assert.Equal("plus", metadata.PlanType);
        Assert.Equal("chatgpt", metadata.AuthMode);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000), metadata.LastRefreshAt);
    }

    [Fact]
    public void RedactsKnownSecretShapes()
    {
        var text = """
        {"access_token":"abc","refresh_token":"def","id_token":"eyJaaa.bbb.ccc","OPENAI_API_KEY":"sk-testSecretSecretSecret"}
        """;

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("def", redacted);
        Assert.DoesNotContain("sk-testSecretSecretSecret", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    private static string CreateJwt(string payloadJson)
    {
        static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        var header = Encode("""{"alg":"none"}""");
        var body = Encode(payloadJson);
        return $"{header}.{body}.signature";
    }
}
