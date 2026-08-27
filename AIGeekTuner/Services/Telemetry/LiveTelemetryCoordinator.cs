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
    /// 录制中：不启动第二套 Hub 轮询，直接镜像 Recorder.LatestSample；
    /// 否则：PeriodicTimer 串行循环（采样完成→下一拍），绝不重叠。
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
                if (IsRecording)
                {
                    Mode = LiveTelemetrySourceMode.Recorder;
                    IsRunning = true;
                    IntervalMs = _recorder!.CurrentSession!.RequestedIntervalMs;
                    MirrorRecorderLatest();
                    return; // 不启动轮询（§22）
                }

                StopLocked();
                IntervalMs = Math.Max(200, intervalMs);
                Mode = LiveTelemetrySourceMode.Coordinator;
                IsRunning = true;
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
                            var snapshot = await _hub.ReadAsync(ct).ConfigureAwait(false);
                            Publish(snapshot);
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

        /// <summary>录制开始时由外部通知：立即切换到镜像模式。</summary>
        public void NotifyRecorderStarted()
        {
            lock (_gate)
            {
                if (!IsRunning || Mode != LiveTelemetrySourceMode.Recorder)
                {
                    return;
                }

                StopLocked();
                Mode = LiveTelemetrySourceMode.Recorder;
                IsRunning = true;
                IntervalMs = _recorder?.CurrentSession?.RequestedIntervalMs ?? IntervalMs;
                MirrorRecorderLatest();
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

        private void HookRecorder()
        {
            if (_recorder is null)
            {
                return;
            }

            _recorder.SampleCaptured -= OnRecorderSample;
            _recorder.SampleCaptured += OnRecorderSample;
        }

        private void UnhookRecorder()
        {
            if (_recorder is not null)
            {
                _recorder.SampleCaptured -= OnRecorderSample;
            }
        }

        private void OnRecorderSample(TelemetrySample sample)
        {
            var sources = _recorder?.CurrentSession?.InitialSources
                ?? (IReadOnlyList<TelemetrySourceReport>)Array.Empty<TelemetrySourceReport>();
            Publish(new TelemetrySnapshot(
                sample.CapturedAtUtc,
                sample.Readings,
                sources,
                Array.Empty<RawTelemetryReading>()));
        }

        private void StopLocked()
        {
            IsRunning = false;
            UnhookRecorder();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }
    }
}


