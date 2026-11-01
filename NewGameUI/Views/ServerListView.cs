using Blazonia.Core;
using Microsoft.AspNetCore.Components.Rendering;

namespace Plugin.Views;

/// <summary>
/// 原生 Avalonia ListBox 的 Blazonia 桥：整张列表（行模板、虚拟化、选中、右键菜单）
/// 都活在 Blazor 渲染树之外，父页面 StateHasChanged 完全不触达行控件。
///
/// 必须走原生的原因（Blazonia.dll 反编译确认）：DataTemplateItemComponent 对 Template
/// 参数不做 diff——父组件每次渲染都会对所有已实现行重执行模板片段、整行控件重建；
/// DataTemplateItemsComponent 的条目根随容器 realize 只增不减——列表越用越卡。
///
/// 用法：razor 里 &lt;ServerListView @ref="..."/&gt;，在 OnAfterRender 里对 List 属性配置
/// ItemTemplate（纯 FuncDataTemplate）/事件/ContextMenu。样式复用 MainMenuWindow.axaml
/// 的 ListBox.serverlist（本控件带同名 Classes）。
/// </summary>
public class ServerListView : NativeControlComponentBase, IElementHandler
{
    private readonly Avalonia.Controls.ListBox _list = new() { Classes = { "serverlist" } };

    /// <summary>原生列表本体（行模板、事件、ContextMenu 由父页面配置）</summary>
    public Avalonia.Controls.ListBox List => _list;

    object IElementHandler.TargetElement => _list;

    protected override void RenderAdditionalElementContent(RenderTreeBuilder builder, ref int sequence)
    {
        // 刻意留空：列表的一切都绕开 Blazor 渲染树
    }
}
