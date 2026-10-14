using System;
using System.IO;
using System.Text.Json;
using EndfieldChargePlus.Customization;
using EndfieldChargePlus.Diagnostics;
using EndfieldChargePlus.Interop;

namespace EndfieldChargePlus.Settings;

public static class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string? _storageDirectoryOverride;

    /// <summary>
    /// 配置读写目录。默认是用户真实的配置目录；测试可以用
    /// <see cref="UseStorageDirectory"/> 把它重定向到隔离目录。
    /// </summary>
    public static string SettingsDirectory => _storageDirectoryOverride ?? AppPaths.DataDirectory;

    /// <summary>
    /// 把配置读写重定向到隔离目录（仅测试使用），释放返回值后恢复。
    /// 故障注入检查因此可以在临时目录里进行，绝不去动用户真实的配置目录 ——
    /// 否则测试自己的创建/删除会在用户目录里留下 delete-pending 残留，反而弄坏保存。
    /// </summary>
    internal static IDisposable UseStorageDirectory(string directory)
    {
        string? previous=_storageDirectoryOverride;
        _storageDirectoryOverride=directory;
        return new StorageScope(previous);
    }

    private sealed class StorageScope(string? previous) : IDisposable
    {
        public void Dispose() => _storageDirectoryOverride=previous;
    }

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    public static string BackupsDirectory => Path.Combine(SettingsDirectory, "Backups");

    private static readonly string[] LegacySettingsPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldChargePlus", "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", AppPaths.PackageFamilyName ?? "unpackaged", "LocalCache", "Local", "EndfieldChargePlus", "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldCharge-CustomHUD", "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldCharge", "settings.json"),
    };

    public static AppSettings Load()
    {
        string? path = File.Exists(SettingsPath)
            ? SettingsPath
            : Array.Find(LegacySettingsPaths, File.Exists);

        if (path is null)
        {
            var firstRun = CreateDefaults() with { HudEnabled = true };
            AppLog.Info("No settings file found. Using first-run defaults.");
            return firstRun;
        }

        try
        {
            AppSettings loaded = DeserializeFile(path);
            var normalized = Normalize(loaded) with
            {
                // Every application launch starts with the HUD master switch ON.
                HudEnabled = true,
            };

            if (!File.Exists(SettingsPath))
            {
                AppLog.Info($"Migrating legacy settings from: {path}");
                Save(normalized);
            }

            return normalized;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to load settings from: {path}", ex);
            BackupCorruptFile(path);

            var fallback = CreateDefaults() with { HudEnabled = true };
            try { Save(fallback); }
            catch (Exception saveEx) { AppLog.Error("Failed to persist fallback settings.", saveEx); }
            return fallback;
        }
    }

    public static AppSettings CreateDefaults()
        => Normalize(new AppSettings());

    public static void Save(AppSettings settings)
    {
        settings = Normalize(settings);
        // 只有配置目录本身是必需的：写不进去必须如实报错。
        Directory.CreateDirectory(SettingsDirectory);

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string temp = SettingsPath + ".tmp";

        try
        {
            // 滚动备份只是辅助手段。Backups 目录可能被杀毒/索引器占用，或在上一次
            // 中断后停留在无法访问的状态；那绝不能阻止用户保存配置本身。
            RefreshPreviousBackup();

            // 优先写临时文件再原子替换，避免崩溃时留下半截配置。但如果连临时文件都
            // 建不出来（目录被安全软件短时占用，或残留的 delete-pending 项），就退回
            // 直接写入：用户不该因为一个暂存文件而完全无法保存。
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, SettingsPath, overwrite: true);
            }
            catch (Exception staging) when (staging is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not stage a temporary settings file; writing in place. {staging.Message}");
                File.WriteAllText(SettingsPath, json);
            }

            AppLog.Info("Settings saved successfully.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            AppLog.Error("Failed to save settings.", ex);
            throw;
        }
    }

    /// <summary>
    /// 刷新 Backups\settings.previous.json。属于尽力而为：失败只记录警告，
    /// 因为保存配置本身并不依赖它。用户在“配置与维护”里主动备份时仍然会如实报错。
    /// </summary>
    private static void RefreshPreviousBackup()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            Directory.CreateDirectory(BackupsDirectory);
            string previous = Path.Combine(BackupsDirectory, "settings.previous.json");
            File.Copy(SettingsPath, previous, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not refresh the settings backup; saving continued. {ex.Message}");
        }
    }

    public static void ExportToFile(string destinationPath, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Destination path is empty.", nameof(destinationPath));

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
        File.WriteAllText(destinationPath, json);
        AppLog.Info($"Settings exported to: {destinationPath}");
    }

    public static AppSettings ImportFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Configuration file does not exist.", sourcePath);

        AppSettings imported = Normalize(DeserializeFile(sourcePath));
        AppLog.Info($"Settings imported from: {sourcePath}");
        return imported;
    }

    public static string? BackupCurrent(string reason = "manual")
    {
        if (!File.Exists(SettingsPath)) return null;

        Directory.CreateDirectory(BackupsDirectory);
        string safeReason = SanitizeFilePart(reason);
        string target = Path.Combine(BackupsDirectory, $"settings.{safeReason}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(SettingsPath, target, overwrite: false);
        TrimBackups();
        AppLog.Info($"Settings backup created: {target}");
        return target;
    }

    private static AppSettings DeserializeFile(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("Configuration JSON did not contain a valid settings object.");
    }

    private static AppSettings Normalize(AppSettings settings)
        => settings with
        {
            HudOpacity = Math.Clamp(settings.HudOpacity, 0.10, 1.0),
            SamplingIntervalSeconds = double.IsFinite(settings.SamplingIntervalSeconds)
                ? Math.Clamp(settings.SamplingIntervalSeconds, 0.1, 60) : 1,
            HudInstances = (settings.HudInstances ?? new()).Where(i => i is not null).Take(15)
                .GroupBy(i => i.Id).Select(g => g.First()).Select(i => i with {
                    Id=string.IsNullOrWhiteSpace(i.Id) ? Guid.NewGuid().ToString("N") : i.Id,
                    Name=string.IsNullOrWhiteSpace(i.Name) ? "HUD" : i.Name.Trim(),
                    Settings=Normalize((i.Settings ?? new()) with { MultiHudEnabled=false, HudInstances=new() }) }).ToList(),
            HudFontFamily = (settings.HudFontFamily ?? "").Trim(),
            ClickToCycle = settings.ClickToCycle,
            UiLanguage = LocalizationManager.NormalizePreference(settings.UiLanguage),
            CustomHud = HudSettingsNormalizer.Normalize(settings.CustomHud ?? CustomHudSettings.CreateDefault()),
        };

    private static void BackupCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(BackupsDirectory);
            string target = Path.Combine(BackupsDirectory, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, target, overwrite: false);
            AppLog.Warn($"Corrupt settings file backed up to: {target}");
            TrimBackups();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to back up the corrupt settings file.", ex);
        }
    }

    private static void TrimBackups()
    {
        try
        {
            foreach (var file in new DirectoryInfo(BackupsDirectory)
                         .GetFiles("settings.*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(20))
            {
                try { file.Delete(); }
                catch { }
            }
        }
        catch { }
    }

    private static string SanitizeFilePart(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');
        return string.IsNullOrWhiteSpace(value) ? "backup" : value.Trim();
    }
}
