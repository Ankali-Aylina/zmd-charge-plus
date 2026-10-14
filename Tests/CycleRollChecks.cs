using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EndfieldChargePlus.Customization;
using EndfieldChargePlus.Interop;
using EndfieldChargePlus.Settings;
using EndfieldChargePlus.Views;

/// <summary>
/// 轮播“向上翻滚”回归检查：切换方案时窗口必须保持可见，
/// 只有胶囊内的内容层在位移，且结束时回到精确的常驻稳态。
/// </summary>
internal static class CycleRollChecks
{
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,Private)!.GetValue(o)!;

    /// <summary>
    /// 与其它检查一样在独立调度循环里运行，并拥有自己的时间预算：
    /// 嵌套进交互检查会挤占它固定的 180 秒预算。
    /// </summary>
    internal static void Standalone(Action<bool,string> check)
    {
        using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(180));Exception? failure=null;bool completed=false;
        Avalonia.Threading.Dispatcher.UIThread.Post(async ()=> { try { await Run(check); completed=true; } catch(Exception ex) { failure=ex; } finally { try { finish.Cancel(); } catch(ObjectDisposedException) { } } });
        Avalonia.Threading.Dispatcher.UIThread.MainLoop(finish.Token);
        if(failure is not null) throw failure;
        if(!completed) throw new TimeoutException("Cycle roll checks did not finish within 180s.");
    }

    internal static async Task Run(Action<bool,string> check)
    {
        check(CustomHudSettings.NormalizeCycleAnimationMode(null)==CustomHudSettings.CycleAnimationRoll
            && CustomHudSettings.NormalizeCycleAnimationMode("")==CustomHudSettings.CycleAnimationRoll
            && CustomHudSettings.NormalizeCycleAnimationMode("nonsense")==CustomHudSettings.CycleAnimationRoll,
            "Unset or unrecognized cycle animations fall back to Roll, so new configurations roll by default.");
        check(CustomHudSettings.NormalizeCycleAnimationMode("simple")==CustomHudSettings.CycleAnimationSimple
            && CustomHudSettings.NormalizeCycleAnimationMode("Full")==CustomHudSettings.CycleAnimationFull
            && CustomHudSettings.NormalizeCycleAnimationMode(" roll ")==CustomHudSettings.CycleAnimationRoll,
            "Simple/Full/Roll are preserved instead of being collapsed into each other.");
        check(CustomHudSettings.CreateDefault().CycleAnimationMode==CustomHudSettings.CycleAnimationRoll,
            "A brand new configuration defaults to the in-place roll cycle animation.");
        check(HudSettingsNormalizer.Normalize(new CustomHudSettings { CycleAnimationMode=CustomHudSettings.CycleAnimationSimple }).CycleAnimationMode==CustomHudSettings.CycleAnimationSimple
            && HudSettingsNormalizer.Normalize(new CustomHudSettings { CycleAnimationMode=CustomHudSettings.CycleAnimationFull }).CycleAnimationMode==CustomHudSettings.CycleAnimationFull,
            "An explicitly saved Simple/Full cycle animation survives normalization unchanged.");
        check(!CustomHudSettings.IsRollCycleAnimation(CustomHudSettings.CycleAnimationSimple)
            && !CustomHudSettings.IsRollCycleAnimation(null)
            && CustomHudSettings.IsRollCycleAnimation(CustomHudSettings.CycleAnimationRoll),
            "Only Roll selects the in-place roll path; the older two keep the retract-and-summon path.");

        var profiles=Enumerable.Range(0,3).Select(i=>new HudProfile {
            Id=$"roll-{i}",Name=$"Roll {i}",PrimaryTemplate=$"ROLL{i}",TitleTemplate="Test",
            SecondaryTemplate="",RightTemplate="50",ProgressVariable="=50",AnimationMode="Full" }).ToList();
        var settings=new AppSettings { AlwaysVisible=true,PersistentLayer=PersistentHudLayer.Topmost,ClickToCycle=true,SamplingIntervalSeconds=.1,
            PositionMode=HudPositionMode.CustomCoordinates,HudCustomX=240,HudCustomY=240,
            CustomHud=new() { Profiles=profiles,ActiveProfileId=profiles[0].Id,
                CycleProfileIds=profiles.Select(p=>p.Id).ToList(),AutoCycle=false,
                CycleAnimationMode=CustomHudSettings.CycleAnimationRoll } };

        var hud=new HudWindow();
        // 记录整个轮播过程中的可见性变化：任何一次 Hide 都会让下面的检查失败。
        bool everHidden=false;
        hud.PropertyChanged+=(_,e)=>{ if(e.Property==Visual.IsVisibleProperty && !hud.IsVisible) everHidden=true; };
        hud.ApplySettings(settings);
        using var runtime=new CustomHudRuntime(hud);
        runtime.ApplySettings(settings);
        runtime.Start();
        await Task.Delay(1200);

        var text=hud.FindControl<TextBlock>("PrimaryText")!;
        var contentHost=hud.FindControl<Grid>("ContentHost")!;
        var scaleHost=hud.FindControl<Grid>("ScaleHost")!;
        var pill=hud.FindControl<Border>("Pill")!;
        check(hud.IsVisible && text.Text=="ROLL0","The persistent HUD settles on the selected profile before the first roll.");

        var handle=hud.TryGetPlatformHandle()!.Handle;

        // 可选：把翻滚过程中的真实画面逐帧存成 PNG（ECP_ROLL_AUDIT=<目录>），
        // 便于人工确认滚动方向与裁切效果。
        string? audit=Environment.GetEnvironmentVariable("ECP_ROLL_AUDIT");
        if(audit is { Length:>0 }) Directory.CreateDirectory(audit);
        Capture(hud,audit,"roll-00-before");

        ClickHud(hud,runtime);

        // 翻滚期间持续采样：滚动是否真的发生、窗口是否一直可见。
        bool sawRolling=false;
        double minOffset=0;
        int shots=1;
        for(int i=0;i<160;i++)
        {
            await Task.Delay(20);
            if(hud.IsContentRolling)
            {
                sawRolling=true;
                minOffset=Math.Min(minOffset,CurrentOffset(contentHost));
                if(shots<12) Capture(hud,audit,$"roll-{shots++:00}-rolling");
            }
            if(sawRolling && !hud.IsContentRolling) break;
        }
        Capture(hud,audit,"roll-99-after");

        check(sawRolling,"Click-to-cycle in Roll mode animates the capsule content in place instead of retracting the HUD.");
        check(!everHidden,"The window is never hidden while the queue advances, so there is no close/reopen flicker.");
        check(minOffset<-1,"The previous content actually rolls up out of the clipped capsule window.");
        check(hud.IsVisible && text.Text=="ROLL1","The roll swaps to the next queue entry inside the same window.");
        check(hud.TryGetPlatformHandle()!.Handle==handle,"The native window handle survives a Roll transition unchanged.");
        check(Math.Abs(CurrentOffset(contentHost))<.001
            && Math.Abs(scaleHost.RenderTransform!.Value.M11-1)<.001
            && pill.Opacity>.99,
            "A completed roll leaves the capsule at the exact persistent steady state.");

        // 再切一次：行为必须与第一次完全一致。
        ClickHud(hud,runtime);
        for(int i=0;i<160 && !hud.IsContentRolling;i++) await Task.Delay(20);
        for(int i=0;i<160 && hud.IsContentRolling;i++) await Task.Delay(20);
        await Task.Delay(150);
        check(!everHidden && hud.IsVisible && text.Text=="ROLL2","Consecutive rolls keep the same window open for every queue entry.");

        // 停止定时器，避免运行时在下面的兜底检查期间重新唤出 HUD。
        runtime.Stop();
        await Task.Delay(150);
        await hud.HideAnimatedAsync();
        check(!hud.IsVisible && !hud.IsContentRolling,"A hidden, non-persistent HUD never reports an in-place roll.");

        // 没有常驻窗口可翻滚时必须安全退回完整唤出流程，而不是留下半截位移。
        var summoned=hud.RollToPersistentAsync(
            HudProfileRenderer.Render(profiles[0],new Dictionary<string,object?>()));
        for(int i=0;i<200 && !hud.IsVisible;i++) await Task.Delay(20);
        check(hud.IsVisible && !hud.IsContentRolling && Math.Abs(CurrentOffset(contentHost))<.001,
            "A roll requested while the HUD is not persistently shown falls back to a normal summon.");
        // 不必为完整的唤出动画等待数秒，取消即可结束这次呈现。
        hud.CancelPresentation();
        await summoned;

        runtime.Dispose();
        hud.Close();
        await Task.Delay(100);
        Console.WriteLine("PASS: in-place roll cycle animation keeps the window open across queue entries");
    }

    private static double CurrentOffset(Grid contentHost)
        => ((TranslateTransform?)contentHost.RenderTransform)?.Y ?? 0d;

    private static void Capture(HudWindow hud,string? directory,string name)
    {
        if(directory is not { Length:>0 }) return;
        using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)hud.Width,(int)hud.Height));
        bitmap.Render(hud);
        bitmap.Save(Path.Combine(directory,name+".png"));
    }

    private static void ClickHud(HudWindow hud,CustomHudRuntime runtime)
    {
        if(!OperatingSystem.IsWindows()) { typeof(CustomHudRuntime).GetMethod("OnHudClicked",Private)!.Invoke(runtime,null); return; }
        var input=Field<WindowsHudInteraction>(hud,"_interaction").InputHandle;
        var pill=hud.FindControl<Border>("Pill")!;
        var point=pill.PointToScreen(new Point(pill.Bounds.Width/2,pill.Bounds.Height/2));
        ScreenToClient(input,ref point);
        var packed=new IntPtr((point.Y<<16)|(point.X&65535));
        SendMessage(input,0x201,new IntPtr(1),packed);
        SendMessage(input,0x202,IntPtr.Zero,packed);
    }

    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h,ref PixelPoint point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
