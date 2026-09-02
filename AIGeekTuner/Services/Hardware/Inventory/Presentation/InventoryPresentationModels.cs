using System;
using System.Collections.Generic;

namespace AIGeekTuner.Services.Hardware.Inventory.Presentation
{
    /// <summary>
    /// V2-M4.5B Gate A/B：静态 Inventory 展示层 DTO。
    /// 纯数据（无 INPC），由 ViewModel 转换为可观察集合；缺失字段在 presenter
    /// 里直接省略行，不产生"未检测到"噪音。
    /// </summary>
    public sealed record InventoryDisplayRow(string Label, string Value);

    public sealed record InventoryDisplayCard(string? Title, IReadOnlyList<InventoryDisplayRow> Rows);

    public sealed record InventoryDisplaySection(string Title, IReadOnlyList<InventoryDisplayCard> Cards);
}
