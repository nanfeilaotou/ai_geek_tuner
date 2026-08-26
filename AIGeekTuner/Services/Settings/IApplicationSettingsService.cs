using AIGeekTuner.Configuration;

namespace AIGeekTuner.Services.Settings
{
    public interface IApplicationSettingsService
    {
        /// <summary>当前已持久化（或默认）的设置快照。</summary>
        ApplicationSettings Current { get; }

        /// <summary>
        /// 校验并原子写入完整设置；磁盘写成功后才更新内存 Current。
        /// 校验失败或 IO 失败抛出 <see cref="ApplicationSettingsException"/>，
        /// 此时磁盘与内存均保持旧值。
        /// </summary>
        Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
    }
}
