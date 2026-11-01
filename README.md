# NewGameUI

用 Avalonia（Blazonia / Razor 页面）重做的 Counter-Strike 1.6 客户端 UI，经 **MetaHook** 注入引擎，
以 **32 位 NativeAOT** 原生 DLL 形式运行，游戏机不需要安装 .NET 运行时。
引擎接入层与 OpenGL2 平台后端在 [GoldSrc.Avalonia](https://github.com/ApertureScienceTeam/GoldSrc.Avalonia)，
当前以本机绝对路径引用，待其发布 NuGet 包后切换为包引用。

| 目录 | 作用 |
|---|---|
| `NewGameUI/` | 插件本体：菜单 UI（Razor 页面）、引擎命令封装、插件入口（Exportfuncs / PluginV2） |
| `Game.targets` 等根配置 | 部署路径与构建约定 |

## 0. 快速开始

1. 安装 .NET 10 SDK，以及 Visual Studio 的“使用 C++ 的桌面开发”工作负载。
2. 修改 `Game.targets` 里的 `CSDir`，指向你的 Half-Life 目录。
3. VS 选 `MHRelease` 生成 `NewGameUI`；把 `NewGameUI.dll` 写进 MetaHook 的 `plugins.lst`（只需一次）。
4. 用 `MetaHook.exe -game cstrike -windowed` 启动游戏。

---

## 1. 环境配置

### 1.1 必装

| 依赖 | 要求 | 检查方式 |
|---|---|---|
| Windows | 10 / 11 x64 | — |
| .NET SDK | **10.0.400+**（目标框架 `net10.0`） | `dotnet --list-sdks` |
| Visual Studio | 2026，或支持 `.slnx` 与 .NET 10 的版本 | — |
| VS 工作负载 | **使用 C++ 的桌面开发**（MSVC v14.5x + Windows SDK） | 发布时能找到 `link.exe` |

> 编译机必须装 MSVC：NativeAOT 要调用 `link.exe` 生成 x86 原生 DLL。
> 目标机（玩游戏那台）不需要 .NET SDK/运行时，AOT + SelfContained 已自带。

### 1.2 还原

```bash
git clone <repo>
cd NewGameUI
dotnet restore NewGameUI.slnx
```

用 VS 打开 `NewGameUI.slnx`。解决方案提供三个构建配置：

| 配置 | 行为 |
|---|---|
| `MHDebug` | Debug，AOT 插件，发布到游戏目录 |
| `MHRelease` | Release，正式发布 |
| `Preview` | 独立桌面程序，不注入引擎，只调 UI 时用 |

### 1.3 环境相关注意事项

- `Directory.Build.targets` 是 Avalonia 12.1.4 `ref` 程序集元数据问题的兼容补丁，**不要删**。
- NuGet 还原需要访问 nuget.org（含 `Blazonia 12.0.0-preview1.3` 预览包）。
- 报 `NETSDK1045` / 找不到 `net10.0`：SDK 太老。
- 发布报找不到 `link.exe`：VS 安装器里补上 C++ 桌面开发工作负载。
- 首次拉取后如果 `GoldSrc.Avalonia` 引用报错：确认 `NewGameUI.csproj` 里
  `GoldSrcAvaloniaDir` 指向的本机路径存在（默认 `D:\workspace\GoldSrc.Avalonia`）。

---

## 2. 游戏目录配置

### 2.1 游戏本体

需要 32 位的 Steam 版 Half-Life，并带 `cstrike`（CS 1.6）。
可以直接指向现有的 Steam 目录；不想污染正版目录的话，先把整个 Half-Life 目录复制一份。

### 2.2 安装 MetaHook

项目按 **MetaHook 0.4.0** 的目录结构加载插件。把 MetaHook 发行包放进 Half-Life 根目录，
至少包含：

```
Half-Life/
├─ MetaHook.exe
├─ SDL2.dll 等随包文件
└─ cstrike/metahook/
   ├─ configs/plugins.lst
   ├─ gamedata/*.json
   └─ plugins/                 （构建产物落点）
```

其他版本 MetaHook 的目录结构可能不同，以发行包为准。

### 2.3 指向你的游戏目录

编辑根目录的 `Game.targets`：

```xml
<CSDir>D:\SteamLibrary\steamapps\common\Half-Life</CSDir>
```

`GameModDir`（`cstrike`）和 `PluginDir`（`cstrike\metahook\plugins`）会由它派生。
这里只影响发布/复制，不影响编译。

如果还要在 VS 里按 F5 调试，同步修改 `NewGameUI.slnx` 中 `MetaHook.exe` 调试项的
`Executable` 和 `StartingDirectory`；启动参数建议保持 `-game cstrike -windowed`。

### 2.4 注册插件（只需手动做一次）

构建只负责把 DLL 复制进 `plugins` 目录，不会改 MetaHook 的插件清单。
编辑 `cstrike/metahook/configs/plugins.lst`，一行一个 DLL 名：

```
NewGameUI.dll
```

### 2.5 最终目录形态

```
Half-Life/
├─ MetaHook.exe
├─ libSkiaSharp.dll            ← 发布时自动复制（x86）
├─ libHarfBuzzSharp.dll        ← 发布时自动复制（x86）
└─ cstrike/
   └─ metahook/
      ├─ configs/plugins.lst   ← 手动加入 NewGameUI.dll
      ├─ gamedata/*.json       ← MetaHook 自带
      └─ plugins/
         └─ NewGameUI.dll   ← 发布时自动复制
```

### 2.6 启动游戏

必须经 MetaHook 启动，不要直接运行 `hl.exe` 或用 Steam 的“开始游戏”：

```bash
"D:\SteamLibrary\steamapps\common\Half-Life\MetaHook.exe" -game cstrike -windowed
```

窗口化便于排查。Steam 版需要 Steam 客户端处于运行状态。

---

## 3. 发布 DLL

发布产物是 **win-x86 / SelfContained / PublishAot** 的原生 DLL，不是普通 .NET 程序集。

### 3.1 Visual Studio

1. 解决方案配置选 `MHRelease`。
2. 生成 `NewGameUI`。
3. 构建结束会自动执行 Publish 并复制到游戏：
   - `NewGameUI.dll` → `$(PluginDir)`（`cstrike\metahook\plugins`）
   - `libSkiaSharp.dll`、`libHarfBuzzSharp.dll` → `$(CSDir)`（Half-Life 根目录）

> 复制逻辑写在 `NewGameUI.csproj` 的 `AfterBuildPublish` / `CopyPublishedFilesToGame` 里：
> Build 后自动 Publish（用 `AfterBuildPublishRunning` 防止 MSBuild 循环依赖），再按上面规则复制。
> 这两个 target 不要删。

### 3.2 命令行

```bash
# 正式发布（对应 MHRelease）
dotnet publish NewGameUI/NewGameUI.csproj -c Release

# 调试插件（对应 MHDebug）
dotnet publish NewGameUI/NewGameUI.csproj -c Debug
```

发布前先关闭游戏：DLL 被占用时复制会失败，但编译本身仍显示成功，容易误判。

### 3.3 Preview（不发布）

只调 UI、不想进游戏时：

```bash
dotnet run --project NewGameUI -c Preview
```

Preview 会以独立桌面窗口运行，不写游戏目录，也不定义 `METAHOOK_PLUGINS`。

### 3.4 AOT 相关约束

- 不要去掉 `RuntimeIdentifier=win-x86` 或 `PublishAot`：引擎是 32 位，x64 插件无法加载。
- 新增窗口/页面的命名空间如果不在 `Plugin.Views` / `Plugin.RazorPages` 下，
  要补进 `NewGameUI/TrimmerRoots.xml`，否则会被 AOT 裁剪掉，运行时找不到类型。
- `Directory.Build.targets` 的 ref→lib 规则和 `AfterBuildPublishRunning` 都是必需的，删了会构建失败或插件不更新。

---

## 4. 验证与排查

### 4.1 日志

`NewGameUI/Plugin/Log.cs` 目前把日志硬编码写到 `D:\mh_plugin.log`，排查时先看这里
（需要的话改 `Path` 常量）。

### 4.2 游戏里还是旧行为

- `cstrike/metahook/plugins/NewGameUI.dll` 的时间戳是否更新。
- `plugins.lst` 里是否有 `NewGameUI.dll`。
- 发布时游戏是否还开着（DLL 被占用，复制静默失败）。
- 是否确实通过 `MetaHook.exe` 启动。

### 4.3 启动崩溃 / 菜单不出现

- Half-Life 根目录是否存在 **x86** 的 `libSkiaSharp.dll`、`libHarfBuzzSharp.dll`；
  手动拷入 64 位版本会直接崩溃，建议始终让 Publish target 复制。
- `cstrike/metahook/gamedata` 是否与当前引擎版本匹配（以 MetaHook 发行包为准）。

### 4.4 构建失败

- `NETSDK1045`、找不到 `net10.0` → 升级 .NET SDK。
- NativeAOT 报 `link.exe` 或 MSVC 相关错误 → 补装 C++ 桌面开发工作负载与 Windows SDK。
- `MSB4006` → `AfterBuildPublish` 的防递归属性被改动过。
