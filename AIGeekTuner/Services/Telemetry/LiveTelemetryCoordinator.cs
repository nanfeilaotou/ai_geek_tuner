using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>数据来源模式：跟随录制器或自行采样。</summary>
    public enum LiveTelemetrySourceMode
    {
        Coordinator,
        Recorder,
    }

    public interface ILiveTelemetrySource
    {
        bool IsRunning { get; }

        LiveTelemetrySourceMode Mode { get; }

        int IntervalMs { get; }

        TelemetrySnapshot? LatestSnapshot { get; }

        /// <summary>新快照到达（后台线程）。订阅方自行调度到 UI 线程。</summary>
        event Action<TelemetrySnapshot>? SnapshotUpdated;

        void Start(int intervalMs);

        void Stop();
    }

    /// <summary>
    /// Hardware 页实时刷新协调器（§18-§23）。
    /// 录制中：不启动第二套 Hub 轮询，由 SampleCaptured 镜像推送（§22）；
    /// 否则：PeriodicTimer 串行循环（采样完成→下一拍），绝不重叠。
    /// 模式切换是自愈的：轮询循环每拍检查 IsRecording，
    /// 录制开始自动跳过 Hub 读取，录制结束自动恢复。
    /// </summary>
    public sealed class LiveTelemetryCoordinator : ILiveTelemetrySource
    {
        private readonly object _gate = new();
        private readonly ITelemetryHub _hub;
        private readonly ITelemetryRecordingService? _recorder;

        private CancellationTokenSource? _cts;
        private Task? _loop;

        public LiveTelemetryCoordinator(ITelemetryHub hub, ITelemetryRecordingService? recorder = null)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _recorder = recorder;

            // §22：镜像采样订阅挂接一次即可。是否真正发布由 IsRunning 把关——
            // 协调器未启动时，录制数据不会泄漏到硬件页。
            if (_recorder is not null)
            {
                _recorder.SampleCaptured += OnRecorderSample;
            }
        }

        public bool IsRunning { get; private set; }

        public LiveTelemetrySourceMode Mode { get; private set; }

        public int IntervalMs { get; private set; }

        public TelemetrySnapshot? LatestSnapshot { get; private set; }

        public event Action<TelemetrySnapshot>? SnapshotUpdated;

        public void Start(int intervalMs)
        {
            lock (_gate)
            {
                StopLocked();
                IntervalMs = Math.Max(200, intervalMs);
                // 录制中启动时不额外轮询 Hub；循环内每拍检测并跳过读取。
                Mode = IsRecording
                    ? LiveTelemetrySourceMode.Recorder
                    : LiveTelemetrySourceMode.Coordinator;
                IsRunning = true;
                if (Mode == LiveTelemetrySourceMode.Recorder)
                {
                    MirrorRecorderLatest();
                }

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                var capturedInterval = IntervalMs;

                _loop = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(capturedInterval));
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            if (IsRecording)
                            {
                                // §22 镜像模式：Recorder 已经在采样，绝不双读 Hub。
                                Mode = LiveTelemetrySourceMode.Recorder;
                            }
                            else
                            {
                                Mode = LiveTelemetrySourceMode.Coordinator;
                                var snapshot = await _hub.ReadAsync(ct).ConfigureAwait(false);
                                Publish(snapshot);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception exception)
                        {
                            ExceptionLogWriter.Write(exception, "LiveTelemetry capture");
                        }

                        try
                        {
                            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }, CancellationToken.None);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                StopLocked();
            }
        }

        private bool IsRecording => _recorder is { IsRecording: true };

        private void MirrorRecorderLatest()
        {
            var session = _recorder?.CurrentSession;
            if (session is null || session.Samples.Count == 0)
            {
                return;
            }

            var last = session.Samples[^1];
            var sources = session.InitialSources.Count > 0
                ? session.InitialSources
                : System.Array.Empty<TelemetrySourceReport>();
            Publish(new TelemetrySnapshot(
                last.CapturedAtUtc,
                last.Readings,
                sources,
                System.Array.Empty<RawTelemetryReading>()));
        }

        private void Publish(TelemetrySnapshot snapshot)
        {
            LatestSnapshot = snapshot;
            SnapshotUpdated?.Invoke(snapshot);
        }

        private void OnRecorderSample(TelemetrySample sample)
        {
            lock (_gate)
            {
                // 只有已启动（无论 Coordinator 还是 Recorder 模式）才镜像推送；
                // 未启动时静默丢弃，避免录制数据绕过硬件页的显示开关。
                if (!IsRunning)
                {
                    return;
                }

                Mode = LiveTelemetrySourceMode.Recorder;
                IntervalMs = _recorder?.CurrentSession?.RequestedIntervalMs ?? IntervalMs;
            }

            var sourceList = _recorder?.CurrentSession?.InitialSources
                ?? (IReadOnlyList<TelemetrySourceReport>)Array.Empty<TelemetrySourceReport>();
            Publish(new TelemetrySnapshot(
                sample.CapturedAtUtc,
                sample.Readings,
                sourceList,
                Array.Empty<RawTelemetryReading>()));
        }

        private void StopLocked()
        {
            IsRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }
    }
}
