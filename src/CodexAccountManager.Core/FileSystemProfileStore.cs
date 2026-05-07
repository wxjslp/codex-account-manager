using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager.Core;

public sealed class FileSystemProfileStore
{
    private static readonly string[] StaticFiles =
    [
        "config.toml",
        "hooks.json",
        "version.json",
        "installation_id",
        "models_cache.json",
        ".personality_migration",
        "AGENTS.md"
    ];

    private static readonly string[] StaticDirectories =
    [
        "agents",
        "prompts",
        "rules",
        "skills",
        "vendor_imports"
    ];

    public FileSystemProfileStore(ManagerPaths paths)
    {
        Paths = paths;
    }

    public ManagerPaths Paths { get; }

    public static string SafeSlug(string text)
    {
        var cleaned = Regex.Replace(text.Trim(), @"[^a-zA-Z0-9._-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "profile" : cleaned;
    }

    public string UniqueProfileKey(string seed)
    {
        var root = Paths.ProfilesDirectory;
        var baseKey = SafeSlug(seed);
        var key = baseKey;
        var index = 2;
        while (Directory.Exists(Path.Combine(root, key)))
        {
            key = $"{baseKey}-{index++}";
        }

        return key;
    }

    public void EnsureProfileHome(string profileHome, string? sourceRoot = null)
    {
        Directory.CreateDirectory(profileHome);
        CopyStaticAssets(profileHome, sourceRoot ?? Paths.GlobalCodexHome);
        ApplyCurrentUserAcl(profileHome);
    }

    public void CopyAuthFile(string sourceAuthFile, string profileHome)
    {
        Directory.CreateDirectory(profileHome);
        AtomicCopy(sourceAuthFile, Path.Combine(profileHome, "auth.json"));
    }

    public void WriteProfileJson(string path, object metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public string BackupAuth(string profileKey, string authPath, string reason)
    {
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException("目标账号没有 auth.json，无法备份。", authPath);
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(Paths.BackupsDirectory, profileKey);
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, $"{stamp}-{SafeSlug(reason)}-auth.json");
        AtomicCopy(authPath, backupPath);
        return backupPath;
    }

    public static void AtomicCopy(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(targetPath)!, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void CopyStaticAssets(string profileHome, string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        foreach (var fileName in StaticFiles)
        {
            var source = Path.Combine(sourceRoot, fileName);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(profileHome, fileName), overwrite: true);
            }
        }

        foreach (var directoryName in StaticDirectories)
        {
            var source = Path.Combine(sourceRoot, directoryName);
            var destination = Path.Combine(profileHome, directoryName);
            if (!Directory.Exists(source) || Directory.Exists(destination))
            {
                continue;
            }

            CopyDirectory(source, destination);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void ApplyCurrentUserAcl(string profileHome)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return;
            }

            var directoryInfo = new DirectoryInfo(profileHome);
            var security = directoryInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            directoryInfo.SetAccessControl(security);
        }
        catch
        {
            // ACL tightening is best-effort. Profile operations must still work on locked-down machines.
        }
    }
}
