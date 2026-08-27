namespace AIGeekTuner.ViewModels
{
    public sealed class AnalysisFindingRow(
        string title,
        string category,
        string assessment,
        string explanation)
    {
        public string Title { get; } = title;
        public string Category { get; } = category;
        public string Assessment { get; } = assessment;
        public string Explanation { get; } = explanation;
    }
}
