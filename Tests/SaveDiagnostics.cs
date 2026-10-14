using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EndfieldChargePlus.Customization;
using EndfieldChargePlus.Settings;
using EndfieldChargePlus.Views;

/// <summary>
/// 保存路径诊断：用“首次运行默认配置”逐步执行真实的保存流程，
/// 逐步打印结果，定位“保存失败”到底发生在哪一步。
/// 运行方式：EndfieldChargePlus.BugTests.exe --save-diag
/// </summary>
internal static class SaveDiagnostics
{
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object? Call(object o,string name,params object?[] args)
        =>o.GetType().GetMethod(name,Private)!.Invoke(o,args);

    // 诊断会构造真实的 SettingsWindow；它的“开机启动”开关会写 HKCU Run 值，
    // 而便携版会把 Environment.ProcessPath 写成目标 —— 在测试进程里那就是测试程序本身。
    // 因此诊断期间强制关闭该开关，并在结束时把注册表还原成进入前的样子。
    private const string RunKeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName="Endfield Charge Plus";

    private static string? ReadRunValue()
    {
        if(!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) as string;
        }
        catch { return null; }
    }

    private static void RestoreRunValue(string? value)
    {
        if(!OperatingSystem.IsWindows()) return;
        try
        {
            using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath,writable:true);
            if(key is null) return;
            if(value is null) key.DeleteValue(RunValueName,throwOnMissingValue:false);
            else key.SetValue(RunValueName,value,Microsoft.Win32.RegistryValueKind.String);
        }
        catch { }
    }

    internal static void Standalone()
    {
        using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(240));Exception? failure=null;
        Dispatcher.UIThread.Post(async ()=>{ try{ await Run(); }catch(Exception ex){ failure=ex; } finally{ finish.Cancel(); } });
        Dispatcher.UIThread.MainLoop(finish.Token);
        if(failure is not null) throw failure;
    }

    private static async Task Run()
    {
        Console.WriteLine("=== SAVE DIAGNOSTICS: first-run defaults ===");
        string settingsPath=SettingsManager.SettingsPath;
        bool existedBefore=File.Exists(settingsPath);
        Console.WriteLine($"data dir     : {SettingsManager.SettingsDirectory}");
        Console.WriteLine($"settings path: {settingsPath} (existed={existedBefore})");

        var settings=SettingsManager.Load();
        string? runBefore=ReadRunValue();
        settings=settings with { StartWithWindows=false };
        Console.WriteLine($"loaded from  : {(File.Exists(settingsPath)?"dev settings.json (real config)":"first-run defaults")}");
        Console.WriteLine($"autostart    : registry before='{runBefore ?? "(none)"}', forced off for this run");
        Console.WriteLine($"defaults     : StartWithWindows={settings.StartWithWindows}, HudEnabled={settings.HudEnabled}, AlwaysVisible={settings.AlwaysVisible}");
        Console.WriteLine($"defaults     : cycle mode={settings.CustomHud.CycleAnimationMode}, profiles={settings.CustomHud.Profiles.Count}, http sources={settings.CustomHud.HttpSources.Count}");

        var hud=new HudWindow();
        hud.ApplySettings(settings);
        using var runtime=new CustomHudRuntime(hud);
        runtime.ApplySettings(settings);
        var window=new SettingsWindow(settings,hud,runtime);
        window.Show();
        await Task.Delay(900);

        // 1) 收集界面配置（内部会走 Customizer.ExportSettings）
        AppSettings? collected=null;
        try
        {
            collected=(AppSettings?)Call(window,"CollectSettingsFromUi");
            Console.WriteLine("STEP1 CollectSettingsFromUi      : OK");
        }
        catch(Exception ex)
        {
            Console.WriteLine($"STEP1 CollectSettingsFromUi      : FAIL {ex.GetType().Name}: {ex.Message}");
        }

        // 2) 开机启动（便携版会写 HKCU Run；这里传 false，避免改动注册表）
        try
        {
            var r=await StartupManager.ApplyAsync(false);
            Console.WriteLine($"STEP2 StartupManager.ApplyAsync  : OK packaged={r.IsPackaged} enabled={r.IsEnabled} state={r.State}");
        }
        catch(Exception ex)
        {
            Console.WriteLine($"STEP2 StartupManager.ApplyAsync  : FAIL {ex.GetType().Name}: {ex.Message}");
        }

        // 3) 写 settings.json
        if(collected is not null)
        {
            try
            {
                SettingsManager.Save(collected);
                Console.WriteLine($"STEP3 SettingsManager.Save       : OK (file exists={File.Exists(settingsPath)})");
            }
            catch(Exception ex)
            {
                Console.WriteLine($"STEP3 SettingsManager.Save       : FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 4) 应用到运行时
        if(collected is not null)
        {
            try
            {
                await runtime.ApplySettingsWithTransitionAsync(collected);
                Console.WriteLine("STEP4 ApplySettingsWithTransition: OK");
            }
            catch(Exception ex)
            {
                Console.WriteLine($"STEP4 ApplySettingsWithTransition: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 5) 真实按钮处理函数（async void，只能轮询结果）
        var saveBtn=window.FindControl<Button>("SaveBtn")!;
        var status=window.FindControl<TextBlock>("MaintenanceStatusText")!;
        string initial=saveBtn.Content?.ToString() ?? "";
        Console.WriteLine($"STEP5 initial button text        : '{initial}'");
        try
        {
            Call(window,"OnSave",saveBtn,new RoutedEventArgs());
        }
        catch(Exception ex)
        {
            Console.WriteLine($"STEP5 OnSave invoke threw        : {ex.GetType().Name}: {ex.Message}");
        }

        for(int i=0;i<120;i++)
        {
            await Task.Delay(250);
            string now=saveBtn.Content?.ToString() ?? "";
            if(now!=initial && !now.Contains("应用中") && !now.Contains("Applying")) break;
        }
        await Task.Delay(200);
        Console.WriteLine($"STEP5 final button text          : '{saveBtn.Content}'");
        Console.WriteLine($"STEP5 maintenance status text    : '{status.Text}'");
        Console.WriteLine($"STEP5 settings.json exists       : {File.Exists(settingsPath)}");

        window.Close();
        runtime.Dispose();
        hud.Close();
        await Task.Delay(300);

        // 还原：诊断不应给开发版留下配置。
        if(!existedBefore)
        {
            try { if(File.Exists(settingsPath)) File.Delete(settingsPath); } catch { }
            try
            {
                string backups=SettingsManager.BackupsDirectory;
                if(Directory.Exists(backups)) Directory.Delete(backups,recursive:true);
            }
            catch { }
            Console.WriteLine("cleanup                          : removed diagnostic settings.json");
        }
        RestoreRunValue(runBefore);
        Console.WriteLine($"autostart    : registry restored to '{ReadRunValue() ?? "(none)"}'");
        Console.WriteLine("=== SAVE DIAGNOSTICS DONE ===");
    }
}
