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
        public HardwareSensorItemViewModel(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }
    }
}
