namespace AIGeekTuner.Services.AI.Providers.Transport
{
    /// <summary>
    /// Profile BaseUrl（API 根地址）→ 具体端点 URI 的唯一入口。
    /// 规则：只做尾斜杠规范化，绝不自动追加 /v1，因此不存在 /v1/v1 重复；
    /// 禁止 query/fragment；允许 http://localhost、http://127.0.0.1、LAN 地址（本地服务就是 HTTP）。
    /// </summary>
    public static class AiEndpointUriBuilder
    {
        public static bool TryCreateRoot(string? rawBaseUrl, out Uri root)
        {
            root = new Uri("http://invalid.invalid/", UriKind.Absolute);
            if (string.IsNullOrWhiteSpace(rawBaseUrl))
            {
                return false;
            }

            if (!Uri.TryCreate(rawBaseUrl.Trim(), UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(parsed.Query)
                || !string.IsNullOrEmpty(parsed.Fragment)
                || !string.IsNullOrEmpty(parsed.UserInfo))
            {
                return false;
            }

            var normalized = parsed.ToString().TrimEnd('/');
            root = new Uri($"{normalized}/", UriKind.Absolute);
            return true;
        }

        public static bool TryCreateModelsUri(string? rawBaseUrl, out Uri modelsUri)
        {
            modelsUri = new Uri("http://invalid.invalid/", UriKind.Absolute);
            return TryCreateRoot(rawBaseUrl, out var root)
                && TryAppend(root, "models", out modelsUri);
        }

        public static bool TryCreateChatCompletionsUri(string? rawBaseUrl, out Uri chatUri)
        {
            chatUri = new Uri("http://invalid.invalid/", UriKind.Absolute);
            return TryCreateRoot(rawBaseUrl, out var root)
                && TryAppend(root, "chat/completions", out chatUri);
        }

        public static bool TryCreateApiPathUri(string? rawBaseUrl, string apiRelativePath, out Uri uri)
        {
            uri = new Uri("http://invalid.invalid/", UriKind.Absolute);
            if (string.IsNullOrWhiteSpace(apiRelativePath) || apiRelativePath.StartsWith('/'))
            {
                return false;
            }

            return TryCreateRoot(rawBaseUrl, out var root)
                && TryAppend(root, apiRelativePath, out uri);
        }

        private static bool TryAppend(Uri root, string relativePath, out Uri result)
        {
            try
            {
                result = new Uri(root, relativePath);
                return true;
            }
            catch (UriFormatException)
            {
                result = new Uri("http://invalid.invalid/", UriKind.Absolute);
                return false;
            }
        }
    }
}
