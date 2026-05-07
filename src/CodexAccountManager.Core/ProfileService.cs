using Microsoft.Data.Sqlite;

namespace CodexAccountManager.Core;

public sealed class ProfileService
{
    private readonly Database _database;
    private readonly FileSystemProfileStore _files;

    public ProfileService(Database database, FileSystemProfileStore? files = null)
    {
        _database = database;
        _files = files ?? new FileSystemProfileStore(database.Paths);
    }

    public IReadOnlyList<AccountProfile> ListProfiles()
    {
        return _database.Query(ProfileSelectSql + " ORDER BY is_active DESC, display_name COLLATE NOCASE", MapProfile);
    }

    public AccountProfile? GetProfile(string id)
    {
        return _database.QuerySingle(
            ProfileSelectSql + " WHERE id = $id",
            MapProfile,
            Database.Param("$id", id));
    }

    public AccountProfile? ActiveProfile()
    {
        return _database.QuerySingle(
            ProfileSelectSql + " WHERE is_active = 1 LIMIT 1",
            MapProfile);
    }

    public AccountProfile CreateProfile(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("账号名不能为空。", nameof(displayName));
        }

        var key = UniqueProfileKey(displayName);
        var profileHome = _database.Paths.ProfileHome(key);
        _files.EnsureProfileHome(profileHome);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        _database.Execute(
            """
            INSERT INTO accounts (
                id, profile_key, display_name, profile_home, is_active, status,
                created_at, updated_at
            ) VALUES (
                $id, $profile_key, $display_name, $profile_home, 0, 'missing_auth',
                $created_at, $updated_at
            )
            """,
            Database.Param("$id", id),
            Database.Param("$profile_key", key),
            Database.Param("$display_name", displayName.Trim()),
            Database.Param("$profile_home", profileHome),
            Database.Param("$created_at", now.ToString("O")),
            Database.Param("$updated_at", now.ToString("O")));

        _files.WriteProfileJson(_database.Paths.ProfileJsonPath(key), new
        {
            id,
            profileKey = key,
            displayName,
            profileHome,
            createdAt = now
        });

        return GetProfile(id)!;
    }

    public AccountProfile ImportCurrentGlobalAccount()
    {
        var authPath = Path.Combine(_database.Paths.GlobalCodexHome, "auth.json");
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException("当前全局 .codex 下没有 auth.json。", authPath);
        }

        var profile = ImportAuthFile(authPath, _database.Paths.GlobalCodexHome);
        SetActive(profile.Id);
        return GetProfile(profile.Id)!;
    }

    public IReadOnlyList<AccountProfile> ImportAuthBackups()
    {
        if (!Directory.Exists(_database.Paths.GlobalCodexHome))
        {
            return [];
        }

        var imported = new List<AccountProfile>();
        foreach (var file in Directory.EnumerateFiles(_database.Paths.GlobalCodexHome, "auth-*.json").Order())
        {
            imported.Add(ImportAuthFile(file));
        }

        return imported;
    }

    public AccountProfile ImportAuthFile(string authPath, string? sourceHome = null)
    {
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException("auth 文件不存在。", authPath);
        }

        var metadata = AuthMetadataParser.FromFile(authPath);
        var duplicate = FindDuplicate(metadata);
        if (duplicate is not null)
        {
            _files.EnsureProfileHome(duplicate.ProfileHome, sourceHome);
            _files.CopyAuthFile(authPath, duplicate.ProfileHome);
            UpdateMetadata(duplicate.Id, metadata, Path.Combine(duplicate.ProfileHome, "auth.json"), "ok", "已导入 auth.json");
            return GetProfile(duplicate.Id)!;
        }

        var seed = $"{metadata.Email ?? metadata.DisplayName}-{metadata.AccountId ?? Guid.NewGuid().ToString("N")[..8]}";
        var key = UniqueProfileKey(seed);
        var profileHome = _database.Paths.ProfileHome(key);
        _files.EnsureProfileHome(profileHome, sourceHome);
        _files.CopyAuthFile(authPath, profileHome);
        var now = DateTimeOffset.UtcNow;
        var rowId = Guid.NewGuid().ToString("N");
        var targetAuth = Path.Combine(profileHome, "auth.json");

        _database.Execute(
            """
            INSERT INTO accounts (
                id, profile_key, account_id, display_name, email, plan_type, auth_mode,
                profile_home, auth_path, is_active, status, status_message, created_at, updated_at,
                last_status_check_at
            ) VALUES (
                $id, $profile_key, $account_id, $display_name, $email, $plan_type, $auth_mode,
                $profile_home, $auth_path, 0, 'ok', '已导入 auth.json', $created_at, $updated_at,
                $last_status_check_at
            )
            """,
            Database.Param("$id", rowId),
            Database.Param("$profile_key", key),
            Database.Param("$account_id", metadata.AccountId),
            Database.Param("$display_name", metadata.DisplayName),
            Database.Param("$email", metadata.Email),
            Database.Param("$plan_type", metadata.PlanType),
            Database.Param("$auth_mode", metadata.AuthMode),
            Database.Param("$profile_home", profileHome),
            Database.Param("$auth_path", targetAuth),
            Database.Param("$created_at", now.ToString("O")),
            Database.Param("$updated_at", now.ToString("O")),
            Database.Param("$last_status_check_at", metadata.LastRefreshAt?.ToString("O")));

        _files.WriteProfileJson(_database.Paths.ProfileJsonPath(key), new
        {
            id = rowId,
            profileKey = key,
            metadata.AccountId,
            metadata.DisplayName,
            metadata.Email,
            metadata.PlanType,
            metadata.AuthMode,
            profileHome,
            sourceAuthFile = authPath,
            createdAt = now
        });

        return GetProfile(rowId)!;
    }

    public AccountProfile RenameProfile(string id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("账号名不能为空。", nameof(displayName));
        }

        _database.Execute(
            "UPDATE accounts SET display_name = $display_name, updated_at = $updated_at WHERE id = $id",
            Database.Param("$display_name", displayName.Trim()),
            Database.Param("$updated_at", DateTimeOffset.UtcNow.ToString("O")),
            Database.Param("$id", id));

        return GetProfile(id) ?? throw new InvalidOperationException("账号不存在。");
    }

    public AccountProfile RefreshProfileAuthMetadata(string id)
    {
        var profile = GetProfile(id) ?? throw new InvalidOperationException("账号不存在。");
        var authPath = Path.Combine(profile.ProfileHome, "auth.json");
        if (!File.Exists(authPath))
        {
            _database.Execute(
                """
                UPDATE accounts
                SET auth_path = NULL,
                    status = 'missing_auth',
                    status_message = '未找到 auth.json',
                    last_status_check_at = $checked_at,
                    updated_at = $updated_at
                WHERE id = $id
                """,
                Database.Param("$checked_at", DateTimeOffset.UtcNow.ToString("O")),
                Database.Param("$updated_at", DateTimeOffset.UtcNow.ToString("O")),
                Database.Param("$id", id));
            return GetProfile(id)!;
        }

        var metadata = AuthMetadataParser.FromFile(authPath);
        UpdateMetadata(id, metadata, authPath, "ok", "已刷新本地 auth.json 元数据");
        return GetProfile(id)!;
    }

    public IReadOnlyList<AccountProfile> RefreshAllProfileAuthMetadata()
    {
        var refreshed = new List<AccountProfile>();
        foreach (var profile in ListProfiles())
        {
            refreshed.Add(RefreshProfileAuthMetadata(profile.Id));
        }

        return refreshed;
    }

    public AccountProfile SetActive(string id)
    {
        var target = GetProfile(id) ?? throw new InvalidOperationException("账号不存在。");
        if (!target.HasAuth)
        {
            throw new InvalidOperationException("该账号没有 auth.json，不能设为当前账号。");
        }

        _database.InTransaction((connection, transaction) =>
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE accounts SET is_active = 0";
            clear.ExecuteNonQuery();

            using var set = connection.CreateCommand();
            set.Transaction = transaction;
            set.CommandText = """
                UPDATE accounts
                SET is_active = 1, updated_at = $updated_at
                WHERE id = $id
                """;
            set.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            set.Parameters.AddWithValue("$id", id);
            set.ExecuteNonQuery();
        });

        return GetProfile(id)!;
    }

    public void DeleteProfile(string id, bool deleteFiles)
    {
        var target = GetProfile(id) ?? throw new InvalidOperationException("账号不存在。");
        _database.Execute("DELETE FROM accounts WHERE id = $id", Database.Param("$id", id));
        var profileDirectory = Path.GetDirectoryName(target.ProfileHome);
        if (deleteFiles
            && !string.IsNullOrWhiteSpace(profileDirectory)
            && IsManagedProfileDirectory(profileDirectory)
            && Directory.Exists(profileDirectory))
        {
            TryDeleteManagedProfileDirectory(profileDirectory);
        }
    }

    public BackupRecord BackupAuth(string id, string reason = "manual")
    {
        var target = GetProfile(id) ?? throw new InvalidOperationException("账号不存在。");
        if (string.IsNullOrWhiteSpace(target.AuthPath))
        {
            throw new InvalidOperationException("该账号没有 auth.json。");
        }

        var backupPath = _files.BackupAuth(target.ProfileKey, target.AuthPath, reason);
        var record = new BackupRecord(Guid.NewGuid().ToString("N"), id, backupPath, reason, DateTimeOffset.UtcNow);
        _database.Execute(
            """
            INSERT INTO backup_records (id, account_id, backup_path, reason, created_at)
            VALUES ($id, $account_id, $backup_path, $reason, $created_at)
            """,
            Database.Param("$id", record.Id),
            Database.Param("$account_id", record.AccountId),
            Database.Param("$backup_path", record.BackupPath),
            Database.Param("$reason", record.Reason),
            Database.Param("$created_at", record.CreatedAt.ToString("O")));
        return record;
    }

    public IReadOnlyList<BackupRecord> ListBackups(string accountId)
    {
        return _database.Query(
            """
            SELECT id, account_id, backup_path, reason, created_at
            FROM backup_records
            WHERE account_id = $account_id
            ORDER BY created_at DESC
            """,
            reader => new BackupRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))),
            Database.Param("$account_id", accountId));
    }

    public AccountProfile RestoreAuth(string accountId, string backupId)
    {
        var profile = GetProfile(accountId) ?? throw new InvalidOperationException("账号不存在。");
        var backup = ListBackups(accountId).FirstOrDefault(item => item.Id == backupId)
            ?? throw new InvalidOperationException("备份记录不存在。");
        if (!File.Exists(backup.BackupPath))
        {
            throw new FileNotFoundException("备份文件不存在。", backup.BackupPath);
        }

        BackupAuth(accountId, "before-restore");
        var targetAuth = Path.Combine(profile.ProfileHome, "auth.json");
        FileSystemProfileStore.AtomicCopy(backup.BackupPath, targetAuth);
        var metadata = AuthMetadataParser.FromFile(targetAuth);
        UpdateMetadata(accountId, metadata, targetAuth, "ok", "已从备份恢复 auth.json");
        return GetProfile(accountId)!;
    }

    public void UpdateStatus(string accountId, string status, string? message)
    {
        _database.Execute(
            """
            UPDATE accounts
            SET status = $status,
                status_message = $status_message,
                last_status_check_at = $last_status_check_at,
                updated_at = $updated_at
            WHERE id = $id
            """,
            Database.Param("$status", status),
            Database.Param("$status_message", SecretRedactor.Redact(message)),
            Database.Param("$last_status_check_at", DateTimeOffset.UtcNow.ToString("O")),
            Database.Param("$updated_at", DateTimeOffset.UtcNow.ToString("O")),
            Database.Param("$id", accountId));
    }

    public void UpdateLiveAccountSnapshot(string accountId, CodexAccountSnapshot snapshot)
    {
        _database.InTransaction((connection, transaction) =>
        {
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            using (var updateAccount = connection.CreateCommand())
            {
                updateAccount.Transaction = transaction;
                updateAccount.CommandText = """
                    UPDATE accounts
                    SET email = COALESCE($email, email),
                        plan_type = COALESCE($plan_type, plan_type),
                        status = 'ok',
                        status_message = $status_message,
                        last_status_check_at = $last_status_check_at,
                        updated_at = $updated_at
                    WHERE id = $id
                    """;
                updateAccount.Parameters.AddWithValue("$email", (object?)snapshot.Email ?? DBNull.Value);
                updateAccount.Parameters.AddWithValue("$plan_type", (object?)snapshot.PlanType ?? DBNull.Value);
                updateAccount.Parameters.AddWithValue("$status_message", "已刷新官方额度信息");
                updateAccount.Parameters.AddWithValue("$last_status_check_at", snapshot.CheckedAt.ToString("O"));
                updateAccount.Parameters.AddWithValue("$updated_at", updatedAt);
                updateAccount.Parameters.AddWithValue("$id", accountId);
                updateAccount.ExecuteNonQuery();
            }

            using (var deleteLimits = connection.CreateCommand())
            {
                deleteLimits.Transaction = transaction;
                deleteLimits.CommandText = "DELETE FROM account_rate_limits WHERE account_id = $account_id";
                deleteLimits.Parameters.AddWithValue("$account_id", accountId);
                deleteLimits.ExecuteNonQuery();
            }

            foreach (var bucket in snapshot.RateLimits)
            {
                using var insertLimit = connection.CreateCommand();
                insertLimit.Transaction = transaction;
                insertLimit.CommandText = """
                    INSERT INTO account_rate_limits (
                        account_id, limit_id, limit_name, plan_type,
                        primary_used_percent, primary_window_duration_mins, primary_resets_at,
                        secondary_used_percent, secondary_window_duration_mins, secondary_resets_at,
                        rate_limit_reached_type, credits_json, checked_at, updated_at
                    ) VALUES (
                        $account_id, $limit_id, $limit_name, $plan_type,
                        $primary_used_percent, $primary_window_duration_mins, $primary_resets_at,
                        $secondary_used_percent, $secondary_window_duration_mins, $secondary_resets_at,
                        $rate_limit_reached_type, $credits_json, $checked_at, $updated_at
                    )
                    """;
                insertLimit.Parameters.AddWithValue("$account_id", accountId);
                insertLimit.Parameters.AddWithValue("$limit_id", bucket.LimitId);
                insertLimit.Parameters.AddWithValue("$limit_name", (object?)bucket.LimitName ?? DBNull.Value);
                insertLimit.Parameters.AddWithValue("$plan_type", (object?)bucket.PlanType ?? DBNull.Value);
                AddWindowParameters(insertLimit, "primary", bucket.Primary);
                AddWindowParameters(insertLimit, "secondary", bucket.Secondary);
                insertLimit.Parameters.AddWithValue("$rate_limit_reached_type", (object?)bucket.RateLimitReachedType ?? DBNull.Value);
                insertLimit.Parameters.AddWithValue("$credits_json", (object?)bucket.CreditsJson ?? DBNull.Value);
                insertLimit.Parameters.AddWithValue("$checked_at", snapshot.CheckedAt.ToString("O"));
                insertLimit.Parameters.AddWithValue("$updated_at", updatedAt);
                insertLimit.ExecuteNonQuery();
            }
        });
    }

    public IReadOnlyList<CodexRateLimitBucket> ListRateLimits(string accountId)
    {
        return _database.Query(
            """
            SELECT limit_id, limit_name, plan_type,
                   primary_used_percent, primary_window_duration_mins, primary_resets_at,
                   secondary_used_percent, secondary_window_duration_mins, secondary_resets_at,
                   rate_limit_reached_type, credits_json
            FROM account_rate_limits
            WHERE account_id = $account_id
            ORDER BY CASE WHEN lower(limit_id) = 'codex' THEN 0 ELSE 1 END,
                     COALESCE(limit_name, limit_id) COLLATE NOCASE
            """,
            MapRateLimitBucket,
            Database.Param("$account_id", accountId));
    }

    private AccountProfile? FindDuplicate(AuthMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.AccountId))
        {
            var byAccountId = _database.QuerySingle(
                ProfileSelectSql + " WHERE account_id = $account_id LIMIT 1",
                MapProfile,
                Database.Param("$account_id", metadata.AccountId));
            if (byAccountId is not null)
            {
                return byAccountId;
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata.Email))
        {
            return _database.QuerySingle(
                ProfileSelectSql + " WHERE email = $email LIMIT 1",
                MapProfile,
                Database.Param("$email", metadata.Email));
        }

        return null;
    }

    private string UniqueProfileKey(string seed)
    {
        var existingKeys = _database
            .Query("SELECT profile_key FROM accounts", reader => reader.GetString(0))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = _database.Paths.ProfilesDirectory;
        var baseKey = FileSystemProfileStore.SafeSlug(seed);
        var key = baseKey;
        var index = 2;
        while (existingKeys.Contains(key) || Directory.Exists(Path.Combine(root, key)))
        {
            key = $"{baseKey}-{index++}";
        }

        return key;
    }

    private bool IsManagedProfileDirectory(string profileDirectory)
    {
        var appRoot = Path.GetFullPath(_database.Paths.AppRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(profileDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.Equals(candidate, appRoot, StringComparison.OrdinalIgnoreCase)
            && candidate.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteManagedProfileDirectory(string profileDirectory)
    {
        try
        {
            Directory.Delete(profileDirectory, recursive: true);
        }
        catch (IOException)
        {
            DeleteUnlockedFiles(profileDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            DeleteUnlockedFiles(profileDirectory);
        }
    }

    private static void DeleteUnlockedFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            try
            {
                Directory.Delete(child, recursive: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void UpdateMetadata(string id, AuthMetadata metadata, string authPath, string status, string message)
    {
        _database.Execute(
            """
            UPDATE accounts
            SET account_id = $account_id,
                display_name = $display_name,
                email = $email,
                plan_type = $plan_type,
                auth_mode = $auth_mode,
                auth_path = $auth_path,
                status = $status,
                status_message = $status_message,
                updated_at = $updated_at,
                last_status_check_at = COALESCE($last_status_check_at, last_status_check_at)
            WHERE id = $id
            """,
            Database.Param("$account_id", metadata.AccountId),
            Database.Param("$display_name", metadata.DisplayName),
            Database.Param("$email", metadata.Email),
            Database.Param("$plan_type", metadata.PlanType),
            Database.Param("$auth_mode", metadata.AuthMode),
            Database.Param("$auth_path", authPath),
            Database.Param("$status", status),
            Database.Param("$status_message", message),
            Database.Param("$updated_at", DateTimeOffset.UtcNow.ToString("O")),
            Database.Param("$last_status_check_at", metadata.LastRefreshAt?.ToString("O")),
            Database.Param("$id", id));
    }

    private static void AddWindowParameters(SqliteCommand command, string prefix, CodexRateLimitWindow? window)
    {
        command.Parameters.AddWithValue($"${prefix}_used_percent", (object?)window?.UsedPercent ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_window_duration_mins", (object?)window?.WindowDurationMins ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_resets_at", (object?)window?.ResetsAt?.ToString("O") ?? DBNull.Value);
    }

    private static CodexRateLimitBucket MapRateLimitBucket(SqliteDataReader reader)
    {
        return new CodexRateLimitBucket(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            MapRateLimitWindow(reader, 3),
            MapRateLimitWindow(reader, 6),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static CodexRateLimitWindow? MapRateLimitWindow(SqliteDataReader reader, int startIndex)
    {
        var hasUsedPercent = !reader.IsDBNull(startIndex);
        var hasDuration = !reader.IsDBNull(startIndex + 1);
        var hasResetsAt = !reader.IsDBNull(startIndex + 2);
        if (!hasUsedPercent && !hasDuration && !hasResetsAt)
        {
            return null;
        }

        return new CodexRateLimitWindow(
            hasUsedPercent ? reader.GetDouble(startIndex) : null,
            hasDuration ? reader.GetInt32(startIndex + 1) : null,
            hasResetsAt ? DateTimeOffset.Parse(reader.GetString(startIndex + 2)) : null);
    }

    private const string ProfileSelectSql = """
        SELECT id, profile_key, account_id, display_name, email, plan_type, auth_mode,
               profile_home, auth_path, is_active, status, status_message, created_at,
               updated_at, last_status_check_at, last_switch_at
        FROM accounts
        """;

    private static AccountProfile MapProfile(SqliteDataReader reader)
    {
        return new AccountProfile
        {
            Id = reader.GetString(0),
            ProfileKey = reader.GetString(1),
            AccountId = reader.IsDBNull(2) ? null : reader.GetString(2),
            DisplayName = reader.GetString(3),
            Email = reader.IsDBNull(4) ? null : reader.GetString(4),
            PlanType = reader.IsDBNull(5) ? null : reader.GetString(5),
            AuthMode = reader.IsDBNull(6) ? null : reader.GetString(6),
            ProfileHome = reader.GetString(7),
            AuthPath = reader.IsDBNull(8) ? null : reader.GetString(8),
            IsActive = reader.GetInt32(9) == 1,
            Status = reader.GetString(10),
            StatusMessage = reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(12)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(13)),
            LastStatusCheckAt = reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
            LastSwitchAt = reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15))
        };
    }
}
