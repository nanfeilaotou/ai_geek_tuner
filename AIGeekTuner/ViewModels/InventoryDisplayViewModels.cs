using System.Collections.Generic;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.ViewModels
{
    /// <summary>
    /// V2-M4.5B Gate A/B：静态 Inventory 展示行/卡片/分区的 ViewModel 包装。
    /// 纯只读展示（数据整批替换，不需要逐行 INPC）。
    /// </summary>
    public sealed class InventoryDisplayRowViewModel
    {
        public InventoryDisplayRowViewModel(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }
    }

    public sealed class InventoryDisplayCardViewModel
    {
        public InventoryDisplayCardViewModel(
            string? title,
            IReadOnlyList<InventoryDisplayRowViewModel> rows)
        {
            Title = title ?? string.Empty;
            HasTitle = title is not null;
            Rows = rows;
        }

        public string Title { get; }

        public bool HasTitle { get; }

        public IReadOnlyList<InventoryDisplayRowViewModel> Rows { get; }
    }

    public sealed class InventoryDisplaySectionViewModel
    {
        public InventoryDisplaySectionViewModel(
            string title,
            IReadOnlyList<InventoryDisplayCardViewModel> cards)
        {
            Title = title;
            Cards = cards;
        }

        public string Title { get; }

        public IReadOnlyList<InventoryDisplayCardViewModel> Cards { get; }
    }
}
