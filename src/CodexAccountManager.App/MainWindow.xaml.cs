using CodexAccountManager.App.ViewModels;
using CodexAccountManager.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CodexAccountManager.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppCrashLog.Write(ex);
            throw;
        }

        _ = RunUiAsync(async () =>
        {
            SetWindowIcon();
            await ViewModel.RefreshAsync();
            SyncSelection();
        });
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "codex-account-manager.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).SetIcon(iconPath);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is AccountProfile profile)
        {
            ViewModel.SelectedProfile = profile;
        }
    }

    private async void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("新建账号", "账号名");
        if (name is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await ViewModel.CreateProfileAsync(name);
            SyncSelection();
        });
    }

    private async void ImportCurrent_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.ImportCurrentAsync();
            SyncSelection();
        });
    }

    private async void ImportAuthFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await ViewModel.ImportAuthFileAsync(file.Path);
            SyncSelection();
        });
    }

    private async void ImportBackups_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.ImportBackupsAsync();
            SyncSelection();
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.RefreshAsync();
            SyncSelection();
        });
    }

    private async void SetActive_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.SetActiveSelectedAsync();
            SyncSelection();
        });
    }

    private async void RefreshQuota_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.RefreshOfficialQuotaSelectedAsync();
            SyncSelection();
        });
    }

    private async void RefreshMetadata_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.RefreshSelectedAuthMetadataAsync();
            SyncSelection();
        });
    }

    private async void OpenGlobalCodexHome_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() =>
        {
            ViewModel.OpenGlobalCodexHome();
            return Task.CompletedTask;
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("重命名", "账号名", defaultText: ViewModel.SelectedProfile?.DisplayName);
        if (name is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await ViewModel.RenameSelectedAsync(name);
            SyncSelection();
        });
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.BackupSelectedAsync();
            SyncSelection();
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var backup = BackupsList.SelectedItem as BackupRecord ?? await PickBackupAsync();
        if (backup is null)
        {
            return;
        }

        var confirmed = await ConfirmAsync("恢复 auth", "恢复前会自动备份当前 auth.json。");
        if (!confirmed)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await ViewModel.RestoreSelectedAsync(backup);
            SyncSelection();
        });
    }

    private async Task<BackupRecord?> PickBackupAsync()
    {
        if (ViewModel.Backups.Count == 0)
        {
            await ShowMessageAsync("恢复 auth", "当前账号没有可恢复的备份。");
            return null;
        }

        var picker = new ComboBox
        {
            ItemsSource = ViewModel.Backups,
            DisplayMemberPath = nameof(BackupRecord.DisplayText),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "选择备份",
            Content = picker,
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? picker.SelectedItem as BackupRecord
            : null;
    }

    private async void SyncGlobal_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await ViewModel.SyncSelectedToGlobalAsync();
            SyncSelection();
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmAsync("删除账号", "同时删除本地 profile 目录。");
        if (!confirmed)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await ViewModel.DeleteSelectedAsync(deleteFiles: true);
            SyncSelection();
        });
    }

    private async void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmAsync("清空日志", "只清空管理器操作日志，不会删除账号或 auth 文件。");
        if (!confirmed)
        {
            return;
        }

        await RunUiAsync(ViewModel.ClearLogsAsync);
    }

    private void SyncSelection()
    {
        ProfilesList.SelectedItem = ViewModel.SelectedProfile;
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("操作失败", ex.Message);
        }
    }

    private async Task<string?> PromptTextAsync(
        string title,
        string placeholder,
        bool isPassword = false,
        string? defaultText = null)
    {
        Control input;
        if (isPassword)
        {
            input = new PasswordBox
            {
                PlaceholderText = placeholder
            };
        }
        else
        {
            input = new TextBox
            {
                PlaceholderText = placeholder,
                Text = defaultText ?? ""
            };
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var value = input switch
        {
            PasswordBox passwordBox => passwordBox.Password,
            TextBox textBox => textBox.Text,
            _ => ""
        };

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "关闭"
        };

        await dialog.ShowAsync();
    }
}
