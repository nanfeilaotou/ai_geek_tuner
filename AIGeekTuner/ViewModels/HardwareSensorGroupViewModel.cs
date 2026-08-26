namespace AIGeekTuner.ViewModels
{
    public sealed class HardwareSensorGroupViewModel
    {
        public HardwareSensorGroupViewModel(
            string title,
            IReadOnlyList<HardwareSensorItemViewModel> items)
        {
            Title = title;
            Items = items;
        }

        public string Title { get; }

        public IReadOnlyList<HardwareSensorItemViewModel> Items { get; }
    }

    public sealed class HardwareSensorItemViewModel
    {
        public HardwareSensorItemViewModel(string label, string value, string? source = null)
        {
            Label = label;
            Value = value;
            Source = source;
        }

        public string Label { get; }

        public string Value { get; }

        /// <summary>来源提示（Tooltip 用）；V1 路径的条目为 null。</summary>
        public string? Source { get; }
    }
}
