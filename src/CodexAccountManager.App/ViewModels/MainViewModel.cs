using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexAccountManager.Core;

namespace CodexAccountManager.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ProfileService _profiles;
    private readonly OperationLogService _logs;
    private readonly CodexAppServerClient _appServer;
    private readonly GlobalSwitchService _globalSwitch;
    private AccountProfile? _selectedProfile;
    private string _statusText = "就绪";
    private bool _isBusy;
    private ProfileSummary _summary = new(0, 0, 0, 0);

    public MainViewModel()
        : this(ManagerPaths.CreateDefault())
    {
    }

    public MainViewModel(ManagerPaths paths)
    {
        Paths = paths;
        Database = new Database(paths);
        _profiles = new ProfileService(Database);
        _logs = new OperationLogService(Database);
        _appServer = new CodexAppServerClient(paths);
        _globalSwitch = new GlobalSwitchService(Database, _profiles);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ManagerPaths Paths { get; }
    public Database Database { get; }
    public ObservableCollection<AccountProfile> Profiles { get; } = [];
    public ObservableCollection<BackupRecord> Backups { get; } = [];
    public ObservableCollection<OperationLogEntry> OperationLogs { get; } = [];
    public ObservableCollection<RateLimitDisplayRow> RateLimitRows { get; } = [];

    public AccountProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedEmail));
            OnPropertyChanged(nameof(SelectedPlan));
            OnPropertyChanged(nameof(SelectedStatus));
            OnPropertyChanged(nameof(SelectedProfileHome));
            OnPropertyChanged(nameof(SelectedAuthPath));
            OnPropertyChanged(nameof(SelectedLastCheck));
            OnPropertyChanged(nameof(SelectedAuthMode));
            OnPropertyChanged(nameof(SelectedAccountId));
            OnPropertyChanged(nameof(SelectedSessionCount));
            OnPropertyChanged(nameof(SelectedLatestSession));
            OnPropertyChanged(nameof(SelectedLaunchCommand));
            RefreshBackups();
            RefreshRateLimits();
        }
    }

    public bool HasSelection => SelectedProfile is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string AppRoot => Paths.AppRoot;
    public string GlobalCodexHome => Paths.GlobalCodexHome;
    public ProfileSummary Summary
    {
        get => _summary;
        private set
        {
            _summary = value;
            OnPropertyChanged();
        }
    }

    public string TotalProfilesText => Summary.TotalText;
    public string ReadyProfilesText => Summary.ReadyText;
    public string MissingAuthProfilesText => Summary.MissingAuthText;
    public string ActiveProfilesText => Summary.ActiveText;
    public string SelectedTitle => SelectedProfile?.DisplayName ?? "未选择";
    public string SelectedEmail => SelectedProfile?.Email ?? "未知";
    public string SelectedPlan => SelectedProfile?.PlanType ?? "未知";
    public string SelectedStatus => SelectedProfile?.StatusMessage ?? SelectedProfile?.Status ?? "未知";
    public string SelectedProfileHome => SelectedProfile?.ProfileHome ?? "";
    public string SelectedAuthPath => SelectedProfile?.AuthPath ?? "";
    public string SelectedAuthMode => SelectedProfile?.AuthMode ?? "未知";
    public string SelectedAccountId => SelectedProfile?.AccountId ?? "未知";
    public string SelectedLastCheck => SelectedProfile?.LastStatusCheckAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
    public string SelectedSessionCount => SelectedProfile is null ? "0" : CountSessions(SelectedProfile.ProfileHome).ToString();
    public string SelectedLatestSession => SelectedProfile is null ? "" : LatestSessionText(SelectedProfile.ProfileHome);
    public string SelectedLaunchCommand => SelectedProfile is null
        ? ""
        : $"$env:CODEX_HOME = '{SelectedProfile.ProfileHome.Replace("'", "''")}'; codex";
    public string QuotaStatusText => RateLimitRows.Count == 0 ? "尚未刷新" : $"已获取 {RateLimitRows.Count} 个额度窗口";

    public async Task RefreshAsync()
    {
        await RunAsync("刷新", () =>
        {
            var selectedId = SelectedProfile?.Id;
            var profiles = _profiles.ListProfiles();
            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(profile);
            }

            RefreshSummary();
            SelectedProfile = Profiles.FirstOrDefault(item => item.Id == selectedId) ?? Profiles.FirstOrDefault();
            RefreshBackups();
            RefreshLogs();
        });
    }

    public async Task CreateProfileAsync(string displayName)
    {
        await RunAsync("新建账号", () =>
        {
            var profile = _profiles.CreateProfile(displayName);
            _logs.Info("新建账号", $"已创建账号档案：{profile.DisplayName}");
            ReplaceProfiles(profile.Id);
        });
    }

    public async Task ImportCurrentAsync()
    {
        await RunAsync("导入当前 .codex", () =>
        {
            var profile = _profiles.ImportCurrentGlobalAccount();
            _logs.Info("导入当前 .codex", $"已导入：{profile.Title}");
            ReplaceProfiles(profile.Id);
        });
    }

    public async Task ImportAuthFileAsync(string path)
    {
        await RunAsync("导入 auth 文件", () =>
        {
            var profile = _profiles.ImportAuthFile(path);
            _logs.Info("导入 auth 文件", $"已导入：{profile.Title}", path);
            ReplaceProfiles(profile.Id);
        });
    }

    public async Task ImportBackupsAsync()
    {
        await RunAsync("导入 auth-*", () =>
        {
            var profiles = _profiles.ImportAuthBackups();
            _logs.Info("导入 auth-*", $"已导入 {profiles.Count} 个账号档案");
            ReplaceProfiles(profiles.LastOrDefault()?.Id);
        });
    }

    public async Task RefreshSelectedAuthMetadataAsync()
    {
        var id = RequireSelected().Id;
        await RunAsync("刷新凭据元数据", () =>
        {
            var profile = _profiles.RefreshProfileAuthMetadata(id);
            _logs.Info("刷新凭据元数据", $"已刷新：{profile.Title}");
            ReplaceProfiles(id);
        });
    }

    public async Task RenameSelectedAsync(string displayName)
    {
        var id = RequireSelected().Id;
        await RunAsync("重命名", () =>
        {
            var profile = _profiles.RenameProfile(id, displayName);
            _logs.Info("重命名", $"账号已重命名：{profile.DisplayName}");
            ReplaceProfiles(id);
        });
    }

    public async Task DeleteSelectedAsync(bool deleteFiles)
    {
        var id = RequireSelected().Id;
        await RunAsync("删除账号", () =>
        {
            _profiles.DeleteProfile(id, deleteFiles);
            _logs.Info("删除账号", deleteFiles ? "已删除账号及本地档案目录" : "已删除账号索引");
            ReplaceProfiles(null);
        });
    }

    public async Task SetActiveSelectedAsync()
    {
        var id = RequireSelected().Id;
        await RunAsync("设为当前", () =>
        {
            var profile = _profiles.SetActive(id);
            _logs.Info("设为当前", $"管理器当前账号：{profile.Title}");
            ReplaceProfiles(id);
        });
    }

    public async Task BackupSelectedAsync()
    {
        var id = RequireSelected().Id;
        await RunAsync("备份 auth", () =>
        {
            var backup = _profiles.BackupAuth(id);
            _logs.Info("备份 auth", "已创建 auth.json 备份", backup.BackupPath);
            RefreshBackups();
            RefreshLogs();
        });
    }

    public async Task RestoreSelectedAsync(BackupRecord backup)
    {
        await RunAsync("恢复 auth", () =>
        {
            var profile = _profiles.RestoreAuth(backup.AccountId, backup.Id);
            _logs.Info("恢复 auth", $"已恢复：{profile.Title}", backup.BackupPath);
            ReplaceProfiles(profile.Id);
        });
    }

    public async Task RefreshOfficialQuotaSelectedAsync()
    {
        var profile = RequireSelected();
        await RunAsync("刷新官方额度", async () =>
        {
            var snapshot = await _appServer.ReadAccountSnapshotAsync(profile);
            _profiles.UpdateLiveAccountSnapshot(profile.Id, snapshot);
            ReplaceProfiles(profile.Id);

            _logs.Info("刷新官方额度", $"已获取 {RateLimitRows.Count} 个额度窗口：{SelectedProfile?.Title ?? profile.Title}");
            RefreshLogs();
        });
    }

    public async Task SyncSelectedToGlobalAsync()
    {
        var id = RequireSelected().Id;
        await RunAsync("同步到全局", () =>
        {
            var result = _globalSwitch.SyncProfileToGlobal(id);
            var message = result.RequiresCodexRestart
                ? $"已覆盖全局 auth.json 为：{result.Profile.Title}。请重启 Codex 后生效。"
                : $"全局 .codex 已同步为：{result.Profile.Title}";
            var detail = result.DeferredFiles.Count == 0
                ? null
                : $"Codex 正在运行，以下辅助文件将在下次同步时再更新：{string.Join(", ", result.DeferredFiles)}";
            _logs.Info("同步到全局", message, detail);
            ReplaceProfiles(id);
        });
    }

    public void OpenGlobalCodexHome()
    {
        Directory.CreateDirectory(Paths.GlobalCodexHome);
        OpenDirectory(Paths.GlobalCodexHome);
    }

    public async Task ClearLogsAsync()
    {
        await RunAsync("清空日志", () =>
        {
            _logs.Clear();
            RefreshLogs();
        });
    }

    private async Task RunAsync(string action, Action actionBody)
    {
        await RunAsync(action, () =>
        {
            actionBody();
            return Task.CompletedTask;
        });
    }

    private async Task RunAsync(string action, Func<Task> actionBody)
    {
        IsBusy = true;
        StatusText = $"{action}...";
        try
        {
            await actionBody();
            StatusText = $"{action}完成";
        }
        catch (Exception ex)
        {
            _logs.Error(action, ex.Message);
            RefreshLogs();
            StatusText = ex.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AccountProfile RequireSelected()
    {
        return SelectedProfile ?? throw new InvalidOperationException("请先选择一个账号。");
    }

    private void ReplaceProfiles(string? selectedId)
    {
        var profiles = _profiles.ListProfiles();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        RefreshSummary();
        SelectedProfile = selectedId is null
            ? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault(item => item.Id == selectedId) ?? Profiles.FirstOrDefault();

        RefreshBackups();
        RefreshLogs();
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        if (SelectedProfile is null)
        {
            return;
        }

        foreach (var backup in _profiles.ListBackups(SelectedProfile.Id))
        {
            Backups.Add(backup);
        }
    }

    private void RefreshRateLimits()
    {
        RateLimitRows.Clear();
        if (SelectedProfile is not null)
        {
            foreach (var row in _profiles.ListRateLimits(SelectedProfile.Id).SelectMany(ToDisplayRows))
            {
                RateLimitRows.Add(row);
            }
        }

        OnPropertyChanged(nameof(QuotaStatusText));
    }

    private void RefreshLogs()
    {
        OperationLogs.Clear();
        foreach (var log in _logs.Recent())
        {
            OperationLogs.Add(log);
        }
    }

    private void RefreshSummary()
    {
        Summary = new ProfileSummary(
            Profiles.Count,
            Profiles.Count(profile => profile.HasAuth && profile.Status != "missing_auth"),
            Profiles.Count(profile => !profile.HasAuth || profile.Status == "missing_auth"),
            Profiles.Count(profile => profile.IsActive));
        OnPropertyChanged(nameof(TotalProfilesText));
        OnPropertyChanged(nameof(ReadyProfilesText));
        OnPropertyChanged(nameof(MissingAuthProfilesText));
        OnPropertyChanged(nameof(ActiveProfilesText));
    }

    private static int CountSessions(string profileHome)
    {
        var sessions = Path.Combine(profileHome, "sessions");
        return Directory.Exists(sessions)
            ? Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static string LatestSessionText(string profileHome)
    {
        var sessions = Path.Combine(profileHome, "sessions");
        if (!Directory.Exists(sessions))
        {
            return "";
        }

        var latest = Directory
            .EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        return latest is null ? "" : latest.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static IEnumerable<RateLimitDisplayRow> ToDisplayRows(CodexRateLimitBucket bucket)
    {
        if (bucket.Primary is not null)
        {
            yield return RateLimitDisplayRow.From(bucket, "主额度", bucket.Primary);
        }

        if (bucket.Secondary is not null)
        {
            yield return RateLimitDisplayRow.From(bucket, "备用额度", bucket.Secondary);
        }

        if (bucket.Primary is null && bucket.Secondary is null)
        {
            yield return new RateLimitDisplayRow(
                bucket.DisplayName,
                "无窗口",
                "未知",
                "未知",
                "",
                bucket.PlanType ?? "",
                bucket.RateLimitReachedType ?? "");
        }
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record RateLimitDisplayRow(
    string Name,
    string Window,
    string Used,
    string Remaining,
    string ResetsAt,
    string PlanType,
    string LimitState)
{
    public static RateLimitDisplayRow From(CodexRateLimitBucket bucket, string label, CodexRateLimitWindow window)
    {
        var windowText = window.WindowDurationMins is null ? label : $"{label} / {FormatDuration(window.WindowDurationMins.Value)}";
        var used = window.UsedPercent is null ? "未知" : $"{window.UsedPercent.Value:0.#}%";
        var remaining = window.RemainingPercent is null ? "未知" : $"{window.RemainingPercent.Value:0.#}%";
        var resetsAt = window.ResetsAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
        return new RateLimitDisplayRow(
            bucket.DisplayName,
            windowText,
            used,
            remaining,
            resetsAt,
            bucket.PlanType ?? "",
            bucket.RateLimitReachedType ?? "");
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes % (60 * 24 * 7) == 0)
        {
            return $"{minutes / (60 * 24 * 7)} 周";
        }

        if (minutes % (60 * 24) == 0)
        {
            return $"{minutes / (60 * 24)} 天";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60} 小时";
        }

        return $"{minutes} 分钟";
    }
}
