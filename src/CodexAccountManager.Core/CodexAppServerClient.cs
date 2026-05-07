using System.Diagnostics;
using System.Text.Json;

namespace CodexAccountManager.Core;

public sealed class CodexAppServerClient
{
    private readonly CodexRuntimeService _runtime;

    public CodexAppServerClient(ManagerPaths paths)
    {
        _runtime = new CodexRuntimeService(paths);
    }

    public async Task<CodexAccountSnapshot> ReadAccountSnapshotAsync(
        AccountProfile profile,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(profile.ProfileHome);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        var executable = _runtime.ResolveCodexExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        startInfo.Environment["CODEX_HOME"] = profile.ProfileHome;
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await SendAsync(process, new
        {
            method = "initialize",
            id = 0,
            @params = new
            {
                clientInfo = new
                {
                    name = "codex_account_manager",
                    title = "Codex Account Manager",
                    version = "0.1.0"
                }
            }
        }, timeout.Token);
        await SendAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
        await SendAsync(process, new { method = "account/read", id = 1, @params = new { refreshToken = true } }, timeout.Token);
        await SendAsync(process, new { method = "account/rateLimits/read", id = 2 }, timeout.Token);

        JsonElement? account = null;
        JsonElement? rateLimits = null;
        while (!timeout.IsCancellationRequested && (account is null || rateLimits is null))
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value))
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"Codex app-server 返回错误：{SecretRedactor.Redact(error.ToString())}");
            }

            if (!root.TryGetProperty("result", out var result))
            {
                continue;
            }

            if (value == 1)
            {
                account = result.Clone();
            }
            else if (value == 2)
            {
                rateLimits = result.Clone();
            }
        }

        TryKill(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
        }

        if (account is null || rateLimits is null)
        {
            var stderr = await ReadStderrAsync(stderrTask);
            throw new TimeoutException(string.IsNullOrWhiteSpace(stderr)
                ? "Codex app-server 未返回账号额度信息。"
                : $"Codex app-server 未返回账号额度信息：{SecretRedactor.Redact(stderr)}");
        }

        return new CodexAccountSnapshot(
            ParseEmail(account.Value),
            ParsePlanType(account.Value),
            ParseRateLimits(rateLimits.Value),
            DateTimeOffset.UtcNow);
    }

    public static IReadOnlyList<CodexRateLimitBucket> ParseRateLimits(JsonElement result)
    {
        var rows = new List<CodexRateLimitBucket>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId)
            && byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byLimitId.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    rows.Add(ParseBucket(property.Value, property.Name));
                }
            }
        }

        if (rows.Count == 0
            && result.TryGetProperty("rateLimits", out var single)
            && single.ValueKind == JsonValueKind.Object)
        {
            rows.Add(ParseBucket(single, "codex"));
        }

        return rows
            .OrderBy(row => row.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static string? ParseEmail(JsonElement accountResult)
    {
        if (accountResult.TryGetProperty("account", out var account)
            && account.ValueKind == JsonValueKind.Object)
        {
            return ReadString(account, "email");
        }

        return null;
    }

    private static string? ParsePlanType(JsonElement accountResult)
    {
        if (accountResult.TryGetProperty("account", out var account)
            && account.ValueKind == JsonValueKind.Object)
        {
            return ReadString(account, "planType");
        }

        return null;
    }

    private static CodexRateLimitBucket ParseBucket(JsonElement bucket, string fallbackLimitId)
    {
        return new CodexRateLimitBucket(
            ReadString(bucket, "limitId") ?? fallbackLimitId,
            ReadString(bucket, "limitName"),
            ReadString(bucket, "planType"),
            ParseWindow(bucket, "primary"),
            ParseWindow(bucket, "secondary"),
            ReadString(bucket, "rateLimitReachedType"),
            bucket.TryGetProperty("credits", out var credits) && credits.ValueKind is not JsonValueKind.Null
                ? credits.GetRawText()
                : null);
    }

    private static CodexRateLimitWindow? ParseWindow(JsonElement bucket, string name)
    {
        if (!bucket.TryGetProperty(name, out var window)
            || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new CodexRateLimitWindow(
            ReadDouble(window, "usedPercent"),
            ReadInt(window, "windowDurationMins"),
            ReadUnixTimestamp(window, "resetsAt"));
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private static async Task<string> ReadStderrAsync(Task<string> stderrTask)
    {
        try
        {
            return await stderrTask;
        }
        catch
        {
            return "";
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
