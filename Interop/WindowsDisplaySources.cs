using System.Runtime.InteropServices;

namespace EndfieldChargePlus.Interop;

/// <summary>Maps GDI display names to VidPn sources, without guessing from DISPLAY1/2 numbering.</summary>
internal static class WindowsDisplaySources
{
    internal static int? SourceId(string gdiName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        for (int attempt=0; attempt<3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2,out uint pathCount,out uint modeCount)!=0) return null;
            var paths=new PathInfo[pathCount]; var modes=new ModeInfo[modeCount];
            int result=QueryDisplayConfig(2,ref pathCount,paths,ref modeCount,modes,IntPtr.Zero);
            if (result==122) continue; // A monitor was added/removed between the two queries.
            if (result!=0) return null;
            var active=paths.Take((int)pathCount).ToArray();
            // PresentMon CSV does not carry adapter LUID. Never conflate equal source IDs on different GPUs.

            foreach (var path in active)
            {
                var name=new SourceName { Header=new Header { Type=1, Size=84, Adapter=path.Source.Adapter, Id=path.Source.Id } };
                if (DisplayConfigGetDeviceInfo(ref name)==0 && string.Equals(name.Name,gdiName,StringComparison.OrdinalIgnoreCase))
                { if(active.Where(p=>p.Source.Id==path.Source.Id).Select(p=>p.Source.Adapter).Distinct().Count()>1) return null; return checked((int)path.Source.Id); }
            }
            return null;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Luid(uint Low,int High);
    [StructLayout(LayoutKind.Sequential)] private struct Source { public Luid Adapter; public uint Id,Mode,Status; }
    [StructLayout(LayoutKind.Sequential)] private struct Target
    { public Luid Adapter; public uint Id,Mode,Technology,Rotation,Scaling,RefreshNumerator,RefreshDenominator,Scanline,Available,Status; }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit,Size=64)] private struct ModeInfo { [FieldOffset(0)] public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type,Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct SourceName
    { public Header Header; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out] PathInfo[] paths,ref uint modeCount,[Out] ModeInfo[] modes,IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName source);
}
