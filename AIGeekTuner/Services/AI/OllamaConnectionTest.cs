namespace AIGeekTuner.Services.AI
{
    public static class OllamaConnectionTest
    {
        private const string VerificationMessage = "请回复Ollama连接成功";

        public static async Task<string> RunAsync(
            IAiService aiService,
            CancellationToken cancellationToken = default)
        {
            if (!await aiService.IsAvailableAsync(cancellationToken))
            {
                throw new OllamaServiceException("本地 Ollama 服务当前不可访问。");
            }

            return await aiService.SendMessageAsync(
                VerificationMessage,
                cancellationToken);
        }
    }
}
