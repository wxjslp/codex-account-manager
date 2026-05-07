using System.Diagnostics;
using System.Text;

namespace CodexAccountManager.Core;

public sealed class CodexRuntimeService
{
    public CodexRuntimeService(ManagerPaths paths)
    {
        Paths = paths;
    }

    public ManagerPaths Paths { get; }

    public string ResolveCodexExecutable()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Environment.ExpandEnvironmentVariables(env)))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(env));
        }

        foreach (var name in new[] { "codex.cmd", "codex.exe", "codex.ps1", "codex" })
        {
            var found = SearchPath(name);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".codex", ".sandbox-bin", "codex.exe"),
            Path.Combine(home, "AppData", "Roaming", "npm", "codex.cmd"),
            Path.Combine(home, "AppData", "Roaming", "npm", "codex.ps1")
        };

        var extensionRoot = Path.Combine(home, ".vscode", "extensions");
        if (Directory.Exists(extensionRoot))
        {
            try
            {
                candidates.AddRange(Directory
                    .EnumerateFiles(extensionRoot, "codex.exe", SearchOption.AllDirectories)
                    .Where(path => path.Contains("openai", StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // Extension scans are opportunistic.
            }
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("未找到 codex 可执行文件。可设置 CODEX_EXE 指向 codex.exe / codex.cmd。");
    }

    public StartedProcess LaunchCodex(AccountProfile profile, string? prompt = null, string? workingDirectory = null)
    {
        EnsureProfileHome(profile);
        var command = BuildPowerShellCommand(profile.ProfileHome, workingDirectory, prompt);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Arguments = $"-NoExit -ExecutionPolicy Bypass -Command {QuotePowerShellString(command)}"
        }) ?? throw new InvalidOperationException("启动 PowerShell 失败。");

        return new StartedProcess(process, command);
    }

    public StartedProcess StartLogin(AccountProfile profile, bool deviceAuth)
    {
        EnsureProfileHome(profile);
        var suffix = deviceAuth ? " login --device-auth" : " login";
        var command = BuildPowerShellCommand(profile.ProfileHome, null, null, suffix);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Arguments = $"-NoExit -ExecutionPolicy Bypass -Command {QuotePowerShellString(command)}"
        }) ?? throw new InvalidOperationException("启动登录进程失败。");

        return new StartedProcess(process, command);
    }

    public async Task<CodexCommandResult> LoginWithApiKeyAsync(
        AccountProfile profile,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key 不能为空。", nameof(apiKey));
        }

        EnsureProfileHome(profile);
        return await RunCodexAsync(
            profile.ProfileHome,
            ["login", "--with-api-key"],
            apiKey + Environment.NewLine,
            TimeSpan.FromMinutes(3),
            cancellationToken);
    }

    public async Task<CodexCommandResult> CheckLoginStatusAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureProfileHome(profile);
        return await RunCodexAsync(profile.ProfileHome, ["login", "status"], null, TimeSpan.FromMinutes(1), cancellationToken);
    }

    private async Task<CodexCommandResult> RunCodexAsync(
        string profileHome,
        IReadOnlyList<string> arguments,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var executable = ResolveCodexExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };

        startInfo.Environment["CODEX_HOME"] = profileHome;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken));
        if (completed != waitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already have exited.
            }

            throw new TimeoutException("Codex 命令执行超时。");
        }

        stopwatch.Stop();
        return new CodexCommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask,
            stopwatch.Elapsed);
    }

    private string BuildPowerShellCommand(string profileHome, string? workingDirectory, string? prompt, string codexSuffix = "")
    {
        var executable = ResolveCodexExecutable();
        var cwd = workingDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var builder = new StringBuilder();
        builder.Append("$env:CODEX_HOME = ");
        builder.Append(QuotePowerShellString(profileHome));
        builder.Append("; Set-Location ");
        builder.Append(QuotePowerShellString(cwd));
        builder.Append("; & ");
        builder.Append(QuotePowerShellString(executable));
        builder.Append(codexSuffix);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder.Append(' ');
            builder.Append(QuotePowerShellString(prompt));
        }

        return builder.ToString();
    }

    private static void EnsureProfileHome(AccountProfile profile)
    {
        Directory.CreateDirectory(profile.ProfileHome);
    }

    private static string QuotePowerShellString(string value) => $"'{value.Replace("'", "''")}'";

    private static string? SearchPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim(), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
