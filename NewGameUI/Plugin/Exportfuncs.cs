using GoldSrc.Avalonia;
using GoldSrc.Avalonia.GL2;
using GoldSrc.HLSDK.Native;
using OpenTK.Graphics.OpenGL;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plugin.Engine;
using NewGameUI;

namespace Plugin;

public unsafe static class Exportfuncs
{

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(cl_enginefuncs_t* pEnginefuncs, int iVersion)
    {
        gEngfuncs = *pEnginefuncs;

        GoldSrc.Avalonia.Global.gEngfuncs = gEngfuncs;
        GL.LoadBindings(new OpenGL2BindingsContext()); ;
        return gExportfuncs.Initialize(pEnginefuncs, iVersion);
    }



    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void HUD_Init()
    {

        gExportfuncs.HUD_Init();
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int HUD_Redraw(float time, int intermission)
    {
        var rtl = gExportfuncs.HUD_Redraw(time, intermission);
        // 游戏内每帧泵 Avalonia 渲染与 Dispatcher（菜单打开时由 EngineHooks::Paint 负责）
        EngineHooks.RenderInGame();
        return rtl;
    }

    static bool inited = false;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void HUD_Frame(double time)
    {
        gExportfuncs.HUD_Frame(time);

    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public unsafe static int HUD_Key_Event(int @event, int @event2, sbyte* c)
    {
        return gExportfuncs.HUD_Key_Event(@event, @event2, c);

    }

}



