using System.Globalization;

namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// Provider 稳定标识的规范与校验：
    /// 小写 ASCII 字母 / 数字 / 连字符，非空，两端必须是字母或数字，
    /// 最长 64 字符。Id 是凭据与配置的关联键，创建后不允许改名；
    /// DisplayName 可随时修改，且永远不作为字典键使用。
    /// </summary>
    public static class AiProviderId
    {
        public const int MaxLength = 64;

        public static bool IsValid(string? value)
        {
            return TryNormalize(value, out _);
        }

        public static bool TryNormalize(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var candidate = value.Trim();
            if (candidate.Length == 0
                || candidate.Length > MaxLength
                || candidate != candidate.ToLowerInvariant())
            {
                return false;
            }

            foreach (var ch in candidate)
            {
                var isLower = ch is >= 'a' and <= 'z';
                var isDigit = ch is >= '0' and <= '9';
                if (!isLower && !isDigit && ch != '-')
                {
                    return false;
                }
            }

            if (candidate.StartsWith('-') || candidate.EndsWith('-'))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }

        /// <summary>面向用户的校验失败说明；不含任何敏感信息。</summary>
        public static string DescribeRule()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Provider ID 只能使用小写字母、数字和连字符（≤{0} 字符，两端不能是连字符），创建后不可修改。",
                MaxLength);
        }
    }
}
