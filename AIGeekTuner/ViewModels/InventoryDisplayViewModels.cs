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


    /// <summary>
    /// V2-M4.5C Gate E/F：实时设备卡行模型。
    /// Meter 行携带已格式化的 Current/Low/High 文本与 visual scale；
    /// Numeric 行不画 bar，仅当前/低/高文本；SubLine 为次级小文本。
    /// </summary>
    public sealed class LiveMeterLineViewModel
    {
        public LiveMeterLineViewModel(
            string label,
            double current,
            double? low,
            double? high,
            double scaleMin,
            double scaleMax,
            string currentText,
            string lowText,
            string highText)
        {
            Label = label;
            Current = current;
            Low = low;
            High = high;
            ScaleMin = scaleMin;
            ScaleMax = scaleMax;
            CurrentText = currentText;
            LowText = lowText;
            HighText = highText;
        }

        public string Label { get; }

        public double Current { get; }

        public double? Low { get; }

        public double? High { get; }

        public double ScaleMin { get; }

        public double ScaleMax { get; }

        public string CurrentText { get; }

        public string LowText { get; }

        public string HighText { get; }
    }

    public sealed class LiveNumericLineViewModel
    {
        public LiveNumericLineViewModel(string label, string current, string? low, string? high)
        {
            Label = label;
            Current = current;
            Low = low;
            High = high;
        }

        public string Label { get; }

        public string Current { get; }

        public string? Low { get; }

        public string? High { get; }
    }

    public sealed class LiveDeviceCardViewModel
    {
        public LiveDeviceCardViewModel(
            string title,
            IReadOnlyList<LiveMeterLineViewModel> meters,
            IReadOnlyList<LiveNumericLineViewModel> numerics,
            IReadOnlyList<string> subLines)
        {
            Title = title;
            Meters = meters;
            Numerics = numerics;
            SubLines = subLines;
        }

        public string Title { get; }

        public IReadOnlyList<LiveMeterLineViewModel> Meters { get; }

        public IReadOnlyList<LiveNumericLineViewModel> Numerics { get; }

        public IReadOnlyList<string> SubLines { get; }
    }
}
