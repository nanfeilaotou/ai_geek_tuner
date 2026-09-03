using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// Profile / Draft 的统一校验入口：返回面向用户的中文错误列表，
    /// 空列表表示通过。不做任何 IO，也不抛异常。
    /// </summary>
    public static class AiProviderProfileValidator
    {
        public const int DisplayNameMaxLength = 80;

        public static IReadOnlyList<string> Validate(AiProviderProfile? profile)
        {
            var errors = new List<string>();
            if (profile is null)
            {
                errors.Add("Provider 配置为空。");
                return errors;
            }

            if (!AiProviderId.TryNormalize(profile.Id, out _))
            {
                errors.Add(AiProviderId.DescribeRule());
            }

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                errors.Add("显示名称不能为空。");
            }
            else if (profile.DisplayName.Trim().Length > DisplayNameMaxLength)
            {
                errors.Add($"显示名称不能超过 {DisplayNameMaxLength} 字符。");
            }

            if (!Enum.IsDefined(profile.Kind))
            {
                errors.Add("未知的 Provider 协议类型。");
            }

            if (!AiEndpointUriBuilder.TryCreateRoot(profile.BaseUrl, out _))
            {
                errors.Add($"服务地址无效：必须是 http:// 或 https:// 开头的完整 URL（例如 http://127.0.0.1:1234/v1）。");
            }

            if (!Enum.IsDefined(profile.StructuredOutputMode))
            {
                errors.Add("未知的结构化输出模式。");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in profile.Models)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Id))
                {
                    errors.Add("模型列表中存在空的模型 ID。");
                    continue;
                }

                if (!seen.Add(model.Id.Trim()))
                {
                    errors.Add($"模型列表中存在重复的模型 ID：{model.Id.Trim()}");
                    continue;
                }

                if (model.Id.Trim().Length > AiProviderModelId.MaxLength)
                {
                    errors.Add($"模型 ID 过长（最多 {AiProviderModelId.MaxLength} 字符）：{model.Id.Trim()}");
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.DefaultModelId)
                && !seen.Contains(profile.DefaultModelId.Trim()))
            {
                errors.Add($"默认模型 {profile.DefaultModelId.Trim()} 不在模型列表中。");
            }

            return errors;
        }
    }
}
