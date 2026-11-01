using GoldSrc.Avalonia;
using Plugin.Engine;
using GoldSrc.HLSDK.Native;
using GoldSrc.Metahook.Native;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NewGameUI;

namespace Plugin
{
    public unsafe static class PluginV2
    {

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
        public static unsafe void Init(nint self, metahook_api_t* pAPI, mh_interface_t* pInterface, mh_enginesave_t* pSave)
        {
            g_pInterface = pInterface;
            g_pMetaHookAPI = pAPI;
            g_pMetaSave = pSave;


            GoldSrc.Avalonia.Global.g_pInterface = Plugin.Global.g_pInterface;
            GoldSrc.Avalonia.Global.g_pMetaHookAPI = Plugin.Global.g_pMetaHookAPI;
            GoldSrc.Avalonia.Global.g_pMetaSave = Plugin.Global.g_pMetaSave;

        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
        public static unsafe void LoadClient(nint self, cl_exportfuncs_t* pExportFunc)
        {
            gExportfuncs = *pExportFunc;
            GoldSrc.Avalonia.Global.gExportfuncs = Plugin.Global.gExportfuncs;
            pExportFunc->Initialize = &Exportfuncs.Initialize;
            pExportFunc->HUD_Init = &Exportfuncs.HUD_Init;
            pExportFunc->HUD_Redraw = &Exportfuncs.HUD_Redraw;
            pExportFunc->HUD_Frame = &Exportfuncs.HUD_Frame;
            pExportFunc->IN_MouseEvent = EngineHooks.MouseEventFn;
            pExportFunc->HUD_Key_Event = &Exportfuncs.HUD_Key_Event;

        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
        public static unsafe void LoadEngine(nint self)
        {
            // 接入层（GoldSrc.Avalonia）不认识插件侧的 App 类型和日志实现，由这里注入
            GoldSrcAvalonia.Log = Log.Write;
            GoldSrcAvalonia.SetAppFactory((w, h, hwnd) => new GoldSrcApplication<App>(w, h, hwnd));
            EngineHooks.Install();
        }


        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
        public static unsafe void ExitGame(nint self, int iResult)
        {

        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
        public static unsafe void Shutdown(nint self)
        {
            EngineHooks.Shutdown();
        }


        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
        public static unsafe void Destructor(nint self)
        {

        }



    }
}
