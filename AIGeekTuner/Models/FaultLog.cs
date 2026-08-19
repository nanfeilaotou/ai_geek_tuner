namespace AIGeekTuner.Models
{
    public sealed class FaultLog
    {
        public string? FileName { get; init; }

        public required string Content { get; init; }

        public long? FileSizeBytes { get; init; }

        public DateTimeOffset? CreatedAt { get; init; }

        public required FaultLogSourceType SourceType { get; init; }

        public string? EncodingName { get; init; }

        public static FaultLog FromPastedText(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException("粘贴的故障日志不能为空。", nameof(content));
            }

            return new FaultLog
            {
                Content = content,
                SourceType = FaultLogSourceType.PastedText
            };
        }
    }
}
