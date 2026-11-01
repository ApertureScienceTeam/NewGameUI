using System.Runtime.InteropServices;

namespace Plugin.Engine;

public readonly record struct VideoMode(int Width, int Height, int BitsPerPixel)
{
    public string Label => $"{Width} x {Height} ({BitsPerPixel}-bit)";
}

public static unsafe class VideoSettings
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode devMode);

    public static IReadOnlyList<VideoMode> GetAvailableModes()
    {
        var result = new List<VideoMode>();
        var devMode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        for (var index = 0; EnumDisplaySettings(null, index, ref devMode); index++)
        {
            if (devMode.PelsWidth is < 320 or > 16384
                || devMode.PelsHeight is < 200 or > 16384
                || devMode.BitsPerPixel is < 8 or > 64)
                continue;

            var mode = new VideoMode((int)devMode.PelsWidth, (int)devMode.PelsHeight, (int)devMode.BitsPerPixel);
            if (!result.Contains(mode))
                result.Add(mode);
        }

        return result
            .OrderBy(mode => mode.Width)
            .ThenBy(mode => mode.Height)
            .ThenBy(mode => mode.BitsPerPixel)
            .ToArray();
    }

    /// <summary>
    /// 用 metahook_api_t 的第 15 槽（GetVideoMode）一次取回分辨率 / 色深 / 窗口化。
    /// 它在引擎 IVideoMode 尚未创建时会回退到注册表与命令行，因此任何时机调用都安全。
    /// </summary>
    public static bool TryGetEngineMode(out VideoMode mode, out bool windowed)
    {
        mode = default;
        windowed = false;

        var api = g_pMetaHookAPI;
        if (api == null)
            return false;

        int width = 0, height = 0, bpp = 0;
        // 用 4 字节 int 接收：即使引擎按 BOOL 写 4 字节，也不会写穿栈上的 bool
        int windowedFlag = 0;
        api->GetVideoMode(&width, &height, &bpp, (bool*)&windowedFlag);

        if (width <= 0 || height <= 0)
            return false;

        mode = new VideoMode(width, height, bpp);
        windowed = windowedFlag != 0;
        return true;
    }

    public static VideoMode? GetCurrentMode()
        => TryGetEngineMode(out var mode, out _) ? mode : null;

    public static bool? IsWindowed()
        => TryGetEngineMode(out _, out var windowed) ? windowed : null;
}
