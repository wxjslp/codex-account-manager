using System.Text.Json;
using CodexAccountManager.Core;

namespace CodexAccountManager.Core.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public void ParseRateLimitsReadsMultiBucketOfficialShape()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1730947200 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "limitName": null,
                  "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1730947200 },
                  "secondary": { "usedPercent": 76, "windowDurationMins": 10080, "resetsAt": 1731542400 },
                  "planType": "plus",
                  "credits": { "remaining": 12 }
                },
                "codex_other": {
                  "limitId": "codex_other",
                  "limitName": "Other",
                  "primary": { "usedPercent": 42, "windowDurationMins": 60, "resetsAt": 1730950800 },
                  "secondary": null
                }
              }
            }
            """);

        var buckets = CodexAppServerClient.ParseRateLimits(document.RootElement);

        Assert.Equal(2, buckets.Count);
        Assert.Equal("codex", buckets[0].LimitId);
        Assert.Equal("plus", buckets[0].PlanType);
        Assert.Equal(25, buckets[0].Primary!.UsedPercent);
        Assert.Equal(75, buckets[0].Primary!.RemainingPercent);
        Assert.Equal(10080, buckets[0].Secondary!.WindowDurationMins);
        Assert.Contains("remaining", buckets[0].CreditsJson);
        Assert.Equal("Other", buckets[1].DisplayName);
    }

    [Fact]
    public void ParseRateLimitsFallsBackToSingleBucketShape()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitId": "codex",
                "limitName": "Codex",
                "primary": { "usedPercent": 100, "windowDurationMins": 15, "resetsAt": 1730947200 },
                "rateLimitReachedType": "usage_limit"
              }
            }
            """);

        var bucket = Assert.Single(CodexAppServerClient.ParseRateLimits(document.RootElement));

        Assert.Equal("Codex", bucket.DisplayName);
        Assert.Equal(0, bucket.Primary!.RemainingPercent);
        Assert.Equal("usage_limit", bucket.RateLimitReachedType);
    }
}
