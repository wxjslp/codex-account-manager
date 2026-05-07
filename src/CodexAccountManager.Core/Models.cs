using System.Diagnostics;

namespace CodexAccountManager.Core;

public sealed record ManagerPaths(
    string AppRoot,
    string GlobalCodexHome)
{
    public string DatabasePath => Path.Combine(AppRoot, "app.db");
    public string LogsDirectory => Directory.CreateDirectory(Path.Combine(AppRoot, "logs")).FullName;
    public string BackupsDirectory => Directory.CreateDirectory(Path.Combine(AppRoot, "backups")).FullName;
    public string ProfilesDirectory => Directory.CreateDirectory(Path.Combine(AppRoot, "profiles")).FullName;

    public static ManagerPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local");
        }

        var globalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(globalHome))
        {
            globalHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return new ManagerPaths(
            Path.Combine(localAppData, "CodexAccountManager"),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(globalHome)));
    }

    public static ManagerPaths CreateForTest(string root)
    {
        return new ManagerPaths(
            Path.Combine(root, "app"),
            Path.Combine(root, "global-codex"));
    }

    public string ProfileDirectory(string profileKey) =>
        Directory.CreateDirectory(Path.Combine(ProfilesDirectory, profileKey)).FullName;

    public string ProfileHome(string profileKey) =>
        Directory.CreateDirectory(Path.Combine(ProfileDirectory(profileKey), "codex-home")).FullName;

    public string ProfileJsonPath(string profileKey) =>
        Path.Combine(ProfileDirectory(profileKey), "profile.json");
}

public sealed record AccountProfile
{
    public required string Id { get; init; }
    public required string ProfileKey { get; init; }
    public string? AccountId { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public string? PlanType { get; init; }
    public string? AuthMode { get; init; }
    public required string ProfileHome { get; init; }
    public string? AuthPath { get; init; }
    public bool IsActive { get; init; }
    public required string Status { get; init; }
    public string? StatusMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastStatusCheckAt { get; init; }
    public DateTimeOffset? LastSwitchAt { get; init; }
    public bool HasAuth => !string.IsNullOrWhiteSpace(AuthPath) && File.Exists(AuthPath);
    public string Title => string.IsNullOrWhiteSpace(Email) ? DisplayName : $"{DisplayName} <{Email}>";
    public string ActiveLabel => IsActive ? "当前" : "";
}

public sealed record AuthMetadata(
    string? AccountId,
    string DisplayName,
    string? Email,
    string? PlanType,
    string? AuthMode,
    DateTimeOffset? LastRefreshAt);

public sealed record OperationLogEntry(
    string Id,
    string Level,
    string Action,
    string Message,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record BackupRecord(
    string Id,
    string AccountId,
    string BackupPath,
    string Reason,
    DateTimeOffset CreatedAt)
{
    public string DisplayText => $"{CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {Reason}";
}

public sealed record GlobalSyncResult(
    AccountProfile Profile,
    bool RequiresCodexRestart,
    IReadOnlyList<string> DeferredFiles);

public sealed record CodexCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;

    public string Summary
    {
        get
        {
            var output = string.IsNullOrWhiteSpace(StandardOutput) ? StandardError : StandardOutput;
            return SecretRedactor.Redact(output).Trim();
        }
    }
}

public sealed record StartedProcess(Process Process, string CommandLine);

public sealed record CodexAccountSnapshot(
    string? Email,
    string? PlanType,
    IReadOnlyList<CodexRateLimitBucket> RateLimits,
    DateTimeOffset CheckedAt);

public sealed record CodexRateLimitBucket(
    string LimitId,
    string? LimitName,
    string? PlanType,
    CodexRateLimitWindow? Primary,
    CodexRateLimitWindow? Secondary,
    string? RateLimitReachedType,
    string? CreditsJson)
{
    public string DisplayName => string.IsNullOrWhiteSpace(LimitName) ? LimitId : LimitName;
}

public sealed record CodexRateLimitWindow(
    double? UsedPercent,
    int? WindowDurationMins,
    DateTimeOffset? ResetsAt)
{
    public double? RemainingPercent => UsedPercent is null ? null : Math.Max(0, 100 - UsedPercent.Value);
}
