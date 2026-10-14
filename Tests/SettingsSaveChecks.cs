using EndfieldChargePlus.Settings;

/// <summary>
/// 保存配置的健壮性检查：Backups 目录不可用时，保存配置本身仍必须成功。
/// 这是真实场景：被杀毒/索引器短暂占用，或上一次中断后目录停留在
/// delete-pending 状态，此时对 Backups 的任何创建都会抛
/// UnauthorizedAccessException / IOException。
/// 全程在隔离的临时目录里做故障注入，绝不触碰用户真实的配置目录。
/// </summary>
internal static class SettingsSaveChecks
{
    internal static void Run(Action<bool,string> check)
    {
        // 检查会在沙箱里故意制造“Backups 不可用”的故障现场，因此必须写在一个
        // 无关紧要的位置。这里选构建输出目录（工作区内），并用随机子目录隔离；
        // 绝不能指向用户真实的配置目录。
        string sandbox=Path.Combine(AppContext.BaseDirectory,"save-check-"+Guid.NewGuid().ToString("N"));
        using var scope=SettingsManager.UseStorageDirectory(sandbox);

        string backups=SettingsManager.BackupsDirectory;
        string settingsPath=SettingsManager.SettingsPath;

        try
        {
            Directory.CreateDirectory(sandbox);
        }
        catch(Exception ex)
        {
            // 受限环境可能不允许创建沙箱。这里明确跳过，而不是把一个环境限制
            // 报成产品缺陷。
            Console.WriteLine($"Save check SKIPPED: cannot create the sandbox ({ex.GetType().Name}: {ex.Message}).");
            return;
        }

        try
        {
            // 全新保存：第一次只写配置文件本身，此时还没有“上一份”可备份。
            bool firstThrew=false;
            try { SettingsManager.Save(SettingsManager.CreateDefaults()); }
            catch(Exception ex) { firstThrew=true; Console.WriteLine($"First save threw: {ex.GetType().Name}: {ex.Message}"); }
            check(!firstThrew && File.Exists(settingsPath),"A first save creates the settings file from scratch.");

            // 第二次保存才会产生“上一份”的滚动备份。
            SettingsManager.Save(SettingsManager.CreateDefaults());
            check(Directory.Exists(backups) && File.Exists(Path.Combine(backups,"settings.previous.json")),
                "A normal save keeps a rolling backup of the previous settings.");

            // 用一个同名文件占住 Backups，让备份目录无法被创建或使用。
            if(Directory.Exists(backups)) Directory.Delete(backups,recursive:true);
            File.WriteAllText(backups,"occupied");

            bool threw=false;
            try { SettingsManager.Save(SettingsManager.CreateDefaults()); }
            catch(Exception ex)
            {
                threw=true;
                Console.WriteLine($"Save threw: {ex.GetType().Name}: {ex.Message}");
            }

            check(!threw,"An unusable Backups folder must never fail the save itself.");
            check(File.Exists(settingsPath),"Settings are still written to disk when the backup folder is unusable.");

            var reloaded=SettingsManager.Load();
            check(reloaded.CustomHud.Profiles.Count>0 && !string.IsNullOrWhiteSpace(reloaded.CustomHud.CycleAnimationMode),
                "Saving while the backup folder is unusable still round-trips into a usable configuration.");

            // 主动备份是用户显式操作，仍然必须如实报错，不能被降级处理掩盖。
            bool manualBackupThrew=false;
            try { SettingsManager.BackupCurrent("regression"); }
            catch { manualBackupThrew=true; }
            check(manualBackupThrew,"An explicit manual backup still reports failure instead of silently succeeding.");

            // 恢复可用的 Backups 目录后，滚动备份必须重新生效。
            File.Delete(backups);
            SettingsManager.Save(SettingsManager.CreateDefaults());
            check(Directory.Exists(backups) && File.Exists(Path.Combine(backups,"settings.previous.json")),
                "Once the folder is usable again, the rolling previous-settings backup resumes.");
        }
        finally
        {
            try { if(Directory.Exists(sandbox)) Directory.Delete(sandbox,recursive:true); } catch { }
        }
    }
}
