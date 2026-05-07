using CodexAccountManager.Core;
using Microsoft.Data.Sqlite;

namespace CodexAccountManager.Core.Tests;

public sealed class ProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cam-tests-{Guid.NewGuid():N}");
    private readonly ManagerPaths _paths;
    private readonly ProfileService _profiles;

    public ProfileServiceTests()
    {
        _paths = ManagerPaths.CreateForTest(_root);
        _profiles = new ProfileService(new Database(_paths));
        Directory.CreateDirectory(_paths.GlobalCodexHome);
    }

    [Fact]
    public void ImportAuthFileCopiesIntoProfileHomeAndLeavesGlobalAuthUntouched()
    {
        var globalAuth = Path.Combine(_paths.GlobalCodexHome, "auth.json");
        File.WriteAllText(globalAuth, ApiKeyAuth("global-secret"));
        var authBackup = Path.Combine(_paths.GlobalCodexHome, "auth-Eli.json");
        File.WriteAllText(authBackup, ChatGptAuth("acct_eli", "eli@example.test"));

        var profile = _profiles.ImportAuthFile(authBackup);

        Assert.True(File.Exists(Path.Combine(profile.ProfileHome, "auth.json")));
        Assert.Equal(File.ReadAllText(authBackup), File.ReadAllText(Path.Combine(profile.ProfileHome, "auth.json")));
        Assert.Equal(ApiKeyAuth("global-secret"), File.ReadAllText(globalAuth));
        Assert.False(profile.IsActive);
    }

    [Fact]
    public void ImportCurrentGlobalAccountMarksOnlyImportedProfileActive()
    {
        File.WriteAllText(Path.Combine(_paths.GlobalCodexHome, "auth.json"), ChatGptAuth("acct_global", "global@example.test"));

        var profile = _profiles.ImportCurrentGlobalAccount();
        var active = _profiles.ActiveProfile();

        Assert.NotNull(active);
        Assert.Equal(profile.Id, active.Id);
        Assert.True(active.HasAuth);
    }

    [Fact]
    public void GlobalSyncCopiesAuthAndRequiresRestartWhenCodexProcessDetectorIsTrue()
    {
        var authBackup = Path.Combine(_paths.GlobalCodexHome, "auth-Eli.json");
        File.WriteAllText(authBackup, ChatGptAuth("acct_eli", "eli@example.test"));
        var profile = _profiles.ImportAuthFile(authBackup);
        var switcher = new GlobalSwitchService(new Database(_paths), _profiles, () => true);

        var synced = switcher.SyncProfileToGlobal(profile.Id);

        Assert.True(synced.RequiresCodexRestart);
        Assert.True(synced.Profile.IsActive);
        Assert.Equal(File.ReadAllText(authBackup), File.ReadAllText(Path.Combine(_paths.GlobalCodexHome, "auth.json")));
    }

    [Fact]
    public void BackupAndRestoreAuthRoundTrip()
    {
        var authBackup = Path.Combine(_paths.GlobalCodexHome, "auth-Eli.json");
        File.WriteAllText(authBackup, ChatGptAuth("acct_eli", "eli@example.test"));
        var profile = _profiles.ImportAuthFile(authBackup);
        var backup = _profiles.BackupAuth(profile.Id);
        File.WriteAllText(Path.Combine(profile.ProfileHome, "auth.json"), ChatGptAuth("acct_other", "other@example.test"));

        var restored = _profiles.RestoreAuth(profile.Id, backup.Id);

        Assert.Equal("eli@example.test", restored.Email);
        Assert.Contains("acct_eli", File.ReadAllText(Path.Combine(restored.ProfileHome, "auth.json")));
    }

    [Fact]
    public void RefreshProfileAuthMetadataUpdatesLocalClaimsAfterLogin()
    {
        var profile = _profiles.CreateProfile("Eli");
        File.WriteAllText(Path.Combine(profile.ProfileHome, "auth.json"), ChatGptAuth("acct_eli", "eli@example.test"));

        var refreshed = _profiles.RefreshProfileAuthMetadata(profile.Id);

        Assert.Equal("eli@example.test", refreshed.Email);
        Assert.Equal("acct_eli", refreshed.AccountId);
        Assert.Equal("ok", refreshed.Status);
        Assert.True(refreshed.HasAuth);
    }

    [Fact]
    public void RefreshProfileAuthMetadataMarksMissingAuth()
    {
        var profile = _profiles.CreateProfile("Empty");

        var refreshed = _profiles.RefreshProfileAuthMetadata(profile.Id);

        Assert.Equal("missing_auth", refreshed.Status);
        Assert.False(refreshed.HasAuth);
        Assert.Equal("未找到 auth.json", refreshed.StatusMessage);
    }

    [Fact]
    public void UpdateLiveAccountSnapshotPersistsRateLimitsAcrossServiceInstances()
    {
        var profile = _profiles.CreateProfile("Quota");
        var snapshot = new CodexAccountSnapshot(
            "quota@example.test",
            "team",
            [
                new CodexRateLimitBucket(
                    "codex",
                    null,
                    "team",
                    new CodexRateLimitWindow(18, 300, DateTimeOffset.Parse("2026-05-08T01:17:00+00:00")),
                    new CodexRateLimitWindow(27, 10080, DateTimeOffset.Parse("2026-05-14T10:07:00+00:00")),
                    null,
                    """{"remaining":12}""")
            ],
            DateTimeOffset.Parse("2026-05-07T12:29:45+00:00"));

        _profiles.UpdateLiveAccountSnapshot(profile.Id, snapshot);
        var reloadedProfiles = new ProfileService(new Database(_paths));

        var reloadedProfile = reloadedProfiles.GetProfile(profile.Id);
        var limits = reloadedProfiles.ListRateLimits(profile.Id);

        Assert.NotNull(reloadedProfile);
        Assert.Equal("team", reloadedProfile.PlanType);
        Assert.Equal("已刷新官方额度信息", reloadedProfile.StatusMessage);
        var limit = Assert.Single(limits);
        Assert.Equal("codex", limit.LimitId);
        Assert.Equal(18, limit.Primary?.UsedPercent);
        Assert.Equal(300, limit.Primary?.WindowDurationMins);
        Assert.Equal(73, limit.Secondary?.RemainingPercent);
        Assert.Contains("remaining", limit.CreditsJson);
    }

    [Fact]
    public void CreateProfileAvoidsDatabaseProfileKeyCollisionsEvenWhenDirectoryIsMissing()
    {
        var first = _profiles.CreateProfile("Same Name");
        Directory.Delete(Path.GetDirectoryName(first.ProfileHome)!, recursive: true);

        var second = _profiles.CreateProfile("Same Name");

        Assert.NotEqual(first.ProfileKey, second.ProfileKey);
        Assert.Equal("Same-Name-2", second.ProfileKey);
    }

    [Fact]
    public void GlobalSyncCopiesSelectedAuthWhenNoCodexProcessIsRunning()
    {
        var authBackup = Path.Combine(_paths.GlobalCodexHome, "auth-Eli.json");
        File.WriteAllText(authBackup, ChatGptAuth("acct_eli", "eli@example.test"));
        var profile = _profiles.ImportAuthFile(authBackup);
        var switcher = new GlobalSwitchService(new Database(_paths), _profiles, () => false);

        var synced = switcher.SyncProfileToGlobal(profile.Id);

        Assert.False(synced.RequiresCodexRestart);
        Assert.True(synced.Profile.IsActive);
        Assert.Equal(File.ReadAllText(authBackup), File.ReadAllText(Path.Combine(_paths.GlobalCodexHome, "auth.json")));
    }

    [Fact]
    public void DeleteProfileRemovesDatabaseRowAndLocalDirectory()
    {
        var profile = _profiles.CreateProfile("Delete Me");
        var profileDirectory = Path.GetDirectoryName(profile.ProfileHome)!;

        _profiles.DeleteProfile(profile.Id, deleteFiles: true);

        Assert.Null(_profiles.GetProfile(profile.Id));
        Assert.False(Directory.Exists(profileDirectory));
    }

    [Fact]
    public void DeleteProfileRemovesDatabaseRowWhenLocalIndexFileIsLocked()
    {
        var profile = _profiles.CreateProfile("Locked Delete");
        var profileDirectory = Path.GetDirectoryName(profile.ProfileHome)!;
        var lockedPath = Path.Combine(profile.ProfileHome, "pack-locked.idx");
        File.WriteAllText(lockedPath, "locked");

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _profiles.DeleteProfile(profile.Id, deleteFiles: true);

            Assert.Null(_profiles.GetProfile(profile.Id));
        }

        if (Directory.Exists(profileDirectory))
        {
            Directory.Delete(profileDirectory, recursive: true);
        }
    }

    [Fact]
    public void DeleteProfileDoesNotRemoveDirectoriesOutsideManagedAppRoot()
    {
        var externalRoot = Path.Combine(_root, "external");
        var externalHome = Path.Combine(externalRoot, "codex-home");
        Directory.CreateDirectory(externalHome);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var database = new Database(_paths);
        database.Execute(
            """
            INSERT INTO accounts (
                id, profile_key, display_name, profile_home, is_active, status, created_at, updated_at
            ) VALUES (
                'external', 'external', 'External', $profile_home, 0, 'missing_auth', $created_at, $updated_at
            )
            """,
            Database.Param("$profile_home", externalHome),
            Database.Param("$created_at", now),
            Database.Param("$updated_at", now));

        _profiles.DeleteProfile("external", deleteFiles: true);

        Assert.Null(_profiles.GetProfile("external"));
        Assert.True(Directory.Exists(externalRoot));
    }

    [Fact]
    public void OperationLogsCanBeCleared()
    {
        var logs = new OperationLogService(new Database(_paths));
        logs.Info("测试", "第一条");
        logs.Error("测试", "第二条");

        logs.Clear();

        Assert.Empty(logs.Recent());
    }

    [Fact]
    public void DatabaseInitializationMigratesLegacyTablesBeforeCreatingIndexes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cam-legacy-{Guid.NewGuid():N}");
        try
        {
            var paths = ManagerPaths.CreateForTest(root);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE accounts (
                        id TEXT PRIMARY KEY,
                        account_id TEXT,
                        display_name TEXT NOT NULL,
                        email TEXT,
                        plan_type TEXT,
                        auth_mode TEXT,
                        profile_home TEXT NOT NULL UNIQUE,
                        auth_path TEXT,
                        is_active INTEGER NOT NULL DEFAULT 0,
                        status TEXT NOT NULL DEFAULT 'ok',
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        last_probe_at TEXT,
                        last_switch_at TEXT,
                        secrets_protected TEXT
                    );
                    CREATE TABLE operation_logs (
                        id TEXT PRIMARY KEY,
                        level TEXT NOT NULL,
                        action TEXT NOT NULL,
                        message TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    CREATE TABLE backup_records (
                        id TEXT PRIMARY KEY,
                        account_id TEXT NOT NULL,
                        backup_path TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    INSERT INTO accounts (id, display_name, profile_home, created_at, updated_at)
                    VALUES ('legacy', 'Legacy', 'C:\legacy\codex-home', '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
                    """;
                command.ExecuteNonQuery();
            }

            var database = new Database(paths);
            var profiles = new ProfileService(database).ListProfiles();
            var logs = new OperationLogService(database);
            logs.Info("迁移", "旧库可写入", "detail");

            Assert.Single(profiles);
            Assert.Equal("Legacy", profiles[0].DisplayName);
            Assert.Equal("Legacy", profiles[0].ProfileKey);
            Assert.Equal("missing_auth", profiles[0].Status);
            Assert.Equal("detail", logs.Recent().Single().Detail);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string ApiKeyAuth(string value) =>
        $$"""{ "auth_mode": "api_key", "OPENAI_API_KEY": "{{value}}" }""";

    private static string ChatGptAuth(string accountId, string email) =>
        $$"""
        {
          "auth_mode": "chatgpt",
          "tokens": {
            "account_id": "{{accountId}}",
            "id_token": "{{FakeJwt(email)}}",
            "access_token": "secret-access",
            "refresh_token": "secret-refresh"
          }
        }
        """;

    private static string FakeJwt(string email)
    {
        static string Encode(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        return $"{Encode("""{"alg":"none"}""")}.{Encode($$"""{ "email": "{{email}}", "name": "{{email}}" }""")}.signature";
    }
}
