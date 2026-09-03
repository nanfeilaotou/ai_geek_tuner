using System;
using System.Collections.Generic;

namespace AIGeekTuner.Services.Hardware.Inventory.Presentation
{
    /// <summary>
    /// V2-M4.5C Gate C：静态 Inventory 展示层 DTO。
    /// 纯数据（无 INPC），由 ViewModel 转换为可观察集合；缺失字段在 presenter
    /// 里直接省略行，不产生"未检测到"噪音。
    /// <see cref="InventoryDisplayRow.Secondary"/> 标记次级/advanced 行
    /// （serial/IP/MAC 等），UI 以更低视觉层级渲染。
    /// Label 为空表示"列表型条目"（音频/网络设备等），UI 整行渲染 Value。
    /// </summary>
    public sealed record InventoryDisplayRow(string Label, string Value, bool Secondary = false);

    public sealed record InventoryDisplayCard(string? Title, IReadOnlyList<InventoryDisplayRow> Rows);

    public sealed record InventoryDisplaySection(string Title, IReadOnlyList<InventoryDisplayCard> Cards);
}
