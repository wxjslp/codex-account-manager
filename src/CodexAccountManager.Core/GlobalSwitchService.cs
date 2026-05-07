using System.Diagnostics;

namespace CodexAccountManager.Core;

public sealed class GlobalSwitchService
{
    private static readonly string[] SwitchFiles =
    [
        "auth.json",
        "cap_sid",
        ".codex-global-state.json"
    ];

    private readonly Database _database;
    private readonly ProfileService _profiles;
    private readonly Func<bool> _codexProcessRunning;

    public GlobalSwitchService(Database database, ProfileService profiles, Func<bool>? codexProcessRunning = null)
    {
        _database = database;
        _profiles = profiles;
        _codexProcessRunning = codexProcessRunning ?? IsCodexProcessRunning;
    }

    public GlobalSyncResult SyncProfileToGlobal(string accountId)
    {
        var requiresRestart = _codexProcessRunning();
        var profile = _profiles.GetProfile(accountId) ?? throw new InvalidOperationException("账号不存在。");
        if (!profile.HasAuth || string.IsNullOrWhiteSpace(profile.AuthPath))
        {
            throw new InvalidOperationException("该账号没有 auth.json，不能同步到全局。");
        }

        ValidateAuthMatchesProfile(profile, profile.AuthPath);

        var globalHome = _database.Paths.GlobalCodexHome;
        Directory.CreateDirectory(globalHome);
        var backupDirectory = Path.Combine(
            _database.Paths.BackupsDirectory,
            "global",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDirectory);

        foreach (var name in SwitchFiles)
        {
            var existing = Path.Combine(globalHome, name);
            if (File.Exists(existing))
            {
                File.Copy(existing, Path.Combine(backupDirectory, name), overwrite: true);
            }
        }

        FileSystemProfileStore.AtomicCopy(profile.AuthPath, Path.Combine(globalHome, "auth.json"));
        var deferredFiles = new List<string>();
        foreach (var name in SwitchFiles.Where(name => name != "auth.json"))
        {
            var source = Path.Combine(profile.ProfileHome, name);
            if (File.Exists(source))
            {
                try
                {
                    FileSystemProfileStore.AtomicCopy(source, Path.Combine(globalHome, name));
                }
                catch (IOException) when (requiresRestart)
                {
                    deferredFiles.Add(name);
                }
                catch (UnauthorizedAccessException) when (requiresRestart)
                {
                    deferredFiles.Add(name);
                }
            }
        }

        ValidateAuthMatchesProfile(profile, Path.Combine(globalHome, "auth.json"));

        _database.Execute(
            """
            UPDATE accounts
            SET last_switch_at = $last_switch_at,
                updated_at = $updated_at
            WHERE id = $id
            """,
            Database.Param("$last_switch_at", DateTimeOffset.UtcNow.ToString("O")),
            Database.Param("$updated_at", DateTimeOffset.UtcNow.ToString("O")),
            Database.Param("$id", profile.Id));

        return new GlobalSyncResult(_profiles.SetActive(profile.Id), requiresRestart, deferredFiles);
    }

    public static bool IsCodexProcessRunning()
    {
        try
        {
            return Process.GetProcesses().Any(process =>
                process.ProcessName.StartsWith("codex", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    private static void ValidateAuthMatchesProfile(AccountProfile profile, string authPath)
    {
        var metadata = AuthMetadataParser.FromFile(authPath);
        if (!string.IsNullOrWhiteSpace(profile.AccountId)
            && string.Equals(profile.AccountId, metadata.AccountId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(profile.Email)
            && string.Equals(profile.Email, metadata.Email, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.AccountId) && string.IsNullOrWhiteSpace(profile.Email))
        {
            return;
        }

        throw new InvalidOperationException("auth.json 与目标账号元数据不一致，已中止同步。");
    }
}
