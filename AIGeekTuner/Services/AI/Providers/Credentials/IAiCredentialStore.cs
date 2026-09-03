namespace AIGeekTuner.Services.AI.Providers.Credentials
{
    /// <summary>
    /// 按 Provider 稳定 Id 存取密钥。实现必须保证：
    /// 磁盘上只有受保护 blob（绝无明文）；missing 返回 null；
    /// 损坏只留痕并视为不可用，绝不允许把应用启动拖垮。
    /// </summary>
    public interface IAiCredentialStore
    {
        /// <summary>写入（或替换）一个 Provider 的密钥。plainTextSecret 不能为空。</summary>
        Task SaveAsync(string providerId, string plainTextSecret, CancellationToken cancellationToken = default);

        /// <summary>读取密钥明文；不存在或不可解密时返回 null。</summary>
        Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken = default);

        /// <summary>删除一个 Provider 的密钥；本来就不存在时是成功 no-op。</summary>
        Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
    }
}
