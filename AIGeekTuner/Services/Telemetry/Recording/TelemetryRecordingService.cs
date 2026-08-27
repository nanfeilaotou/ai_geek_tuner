using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    public interface ITelemetryRecordingService
    {
        bool IsRecording { get; }

        /// <summary>活动会话的实时引用（UI 以只读方式轮询，不做并发采样）。</summary>
        TelemetryRecordingSession? CurrentSession { get; }

        TelemetrySample? LatestSample { get; }

        string? LastError { get; }

        /// <summary>最近一次 finalize 的保存路径（store 未配置或失败时为 null）。</summary>
        string? LastSavedPath { get; }

        /// <summary>每个新采样追加后触发（后台线程）——供实时视图共享采样（§22）。</summary>
        event Action<TelemetrySample>? SampleCaptured;

        /// <summary>开始录制；已有活动会话或间隔非法时返回 false（§35）。</summary>
        bool Start(int intervalMs);

        /// <summary>停止并 finalize（分析 + 原子保存）。未在录制时返回当前/最后会话。</summary>
        Task<TelemetryRecordingSession?> StopAsync();

        /// <summary>应用退出时的 best-effort 收尾；不阻塞超过 timeout。</summary>
        Task FinalizeIfRecordingAsync(TimeSpan timeout);
    }

    /// <summary>
    /// 手动录制服务（composition root 单实例，生命周期独立于页面，§33）。
    ///
    /// 数据原则（§1/§2）：只记录 Hub 已选择的 canonical 读数——目标是趋势、统计、
    /// 异常时间点与来源切换，而不是复制 HWiNFO/AIDA 的完整数据库。
    ///
    /// 调度规则（§4）：PeriodicTimer 串行循环，“采样完成→等待下一拍”，
    /// 读取慢于间隔也不会产生并发 telemetry read。每个 sample 记录真实 CapturedAtUtc（§5）。
    /// </summary>
    public sealed class TelemetryRecordingService : ITelemetryRecordingService
    {
        /// <summary>6 小时 × 1 秒 = 21600：达到上限自动正常收尾，绝不 OOM（§14）。</summary>
        public const int MaxSamples = 21_600;

        private readonly object _gate = new();
        private readonly ITelemetryHub _hub;
        private readonly ITelemetrySessionStore? _store;
        private readonly int _maxSamples;

        private TelemetryRecordingSession? _session;
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _stopRequested;

        // 事件检测状态（跨采样）
        private readonly Dictionary<TelemetrySourceKind, TelemetrySourceStatus> _lastSourceStatus = new();
        private readonly Dictionary<(TelemetryDeviceKind, string, string), TelemetrySourceKind> _lastMetricSource = new();
        private DateTimeOffset? _lastSuccessfulCaptureAtUtc;
        private int _sequence;

        public TelemetryRecordingService(
            ITelemetryHub hub,
            ITelemetrySessionStore? store = null,
            int? maxSamplesOverride = null)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _store = store;
            _maxSamples = maxSamplesOverride ?? MaxSamples;
        }

        /// <summary>最近一次 finalize 的保存路径；保存失败或未配置 store 时为 null。</summary>
        public string? LastSavedPath { get; private set; }
        public event Action<TelemetrySample>? SampleCaptured;

        public bool IsRecording
        {
            get
            {
                lock (_gate)
                {
                    return _session is not null && _session.Status == RecordingStatus.Recording;
                }
            }
        }

        public TelemetryRecordingSession? CurrentSession
        {
            get
            {
                lock (_gate)
                {
                    return _session;
                }
            }
        }

        public TelemetrySample? LatestSample
        {
            get
            {
                lock (_gate)
                {
                    return _session?.Samples.Count > 0 ? _session.Samples[^1] : null;
                }
            }
        }

        public string? LastError { get; private set; }

        public bool Start(int intervalMs)
        {
            if (intervalMs is < 200 or > 5000)
            {
                return false;
            }

            lock (_gate)
            {
                if (IsRecording)
                {
                    return false; // 单活动会话（§35）
                }

                _sequence = 0;
                _lastSourceStatus.Clear();
                _lastMetricSource.Clear();
                _lastSuccessfulCaptureAtUtc = null;
                _stopRequested = false;
                LastError = null;
                _session = TelemetryRecordingSession.Start(intervalMs, DateTimeOffset.UtcNow);
                _loopCts = new CancellationTokenSource();
                var ct = _loopCts.Token;
                var capturedInterval = intervalMs; // 间隔快照：录制期间不受设置变化影响（§39）

                _loopTask = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(capturedInterval));
                    while (!ct.IsCancellationRequested && !_stopRequested)
                    {
                        try
                        {
                            var sample = await CaptureOnceAsync(_session!, ct);
                            _session!.AddSample(sample);
                            SampleCaptured?.Invoke(sample);
                            if (_session!.Samples.Count >= _maxSamples)
                            {
                                // 上限保护：自动正常收尾（§14），包括分析与落盘。
                                var finalized = FinalizeCoreLocked(RecordingStatus.Completed);
                                SaveIfPossible(finalized);
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception exception)
                        {
                            // 单轮失败不终止会话：记 SampleGap 继续下一轮（§37）。
                            ExceptionLogWriter.Write(exception, "Telemetry/recorder capture");
                            _session!.AddEvent(TelemetrySessionEvent.Simple(
                                TelemetrySessionEventType.SampleGap,
                                DateTimeOffset.UtcNow,
                                $"capture failed: {exception.Message}"));
                        }

                        try
                        {
                            if (!await timer.WaitForNextTickAsync(ct))
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

                return true;
            }
        }

        public async Task<TelemetryRecordingSession?> StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                if (!IsRecording)
                {
                    return _session;
                }

                _stopRequested = true;
                _loopCts?.Cancel();
                loop = _loopTask;
            }

            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    ExceptionLogWriter.Write(exception, "Telemetry/recorder loop");
                }
            }

            TelemetryRecordingSession? finalized;
            lock (_gate)
            {
                finalized = FinalizeCoreLocked(RecordingStatus.Completed);
            }

            SaveIfPossible(finalized);
            return finalized;
        }

        public async Task FinalizeIfRecordingAsync(TimeSpan timeout)
        {
            if (!IsRecording)
            {
                return;
            }

            var finalize = StopAsync();
            await Task.WhenAny(finalize, Task.Delay(timeout)).ConfigureAwait(false);
        }

        private TelemetryRecordingSession? FinalizeCoreLocked(RecordingStatus status)
        {
            var session = _session;
            if (session is null || session.Status != RecordingStatus.Recording)
            {
                return session;
            }

            var summary = TelemetrySessionAnalyzer.Analyze(session);
            var finalized = session with
            {
                Status = status,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Summary = summary,
            };
            _session = finalized;
            return finalized;
        }

        private void SaveIfPossible(TelemetryRecordingSession? session)
        {
            if (session is null || _store is null)
            {
                return;
            }

            try
            {
                LastSavedPath = _store.Save(session);
            }
            catch (Exception exception)
            {
                LastSavedPath = null;
                ExceptionLogWriter.Write(exception, "Telemetry/session save");
            }
        }

        // ---- 内部：单次捕获（internal 供确定性测试直接驱动，§42） ----

        public async Task<TelemetrySample> CaptureOnceAsync(
            TelemetryRecordingSession session,
            CancellationToken cancellationToken)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var snapshot = await _hub.ReadAsync(cancellationToken).ConfigureAwait(false);
            var completedAt = DateTimeOffset.UtcNow;

            var sequence = Interlocked.Increment(ref _sequence);
            var sample = new TelemetrySample(
                sequence,
                completedAt,
                (long)(completedAt - startedAt).TotalMilliseconds,
                snapshot.CanonicalReadings);

            RecordSourceEvents(session, snapshot.Sources, completedAt);
            RecordMetricSourceEvents(session, snapshot.CanonicalReadings, completedAt);

            if (snapshot.CanonicalReadings.Count == 0)
            {
                session.AddEvent(TelemetrySessionEvent.Simple(
                    TelemetrySessionEventType.SampleGap,
                    completedAt,
                    "no canonical data this round"));
            }
            else
            {
                _lastSuccessfulCaptureAtUtc = completedAt;
                session.SetInitialSources(snapshot.Sources);
            }

            return sample;
        }

        private void SetInitialSources(IReadOnlyList<TelemetrySourceReport> sources)
        {
            lock (_gate)
            {
                if (_session is not null && _session.InitialSources.Count == 0)
                {
                    _session = _session with { InitialSources = sources };
                }
            }
        }

        private void RecordSourceEvents(
            TelemetryRecordingSession session,
            IReadOnlyList<TelemetrySourceReport> reports,
            DateTimeOffset at)
        {
            foreach (var report in reports)
            {
                if (!_lastSourceStatus.TryGetValue(report.Source, out var previous))
                {
                    _lastSourceStatus[report.Source] = report.Status;
                    continue;
                }

                if (previous != report.Status)
                {
                    var type = report.Status == TelemetrySourceStatus.Unavailable
                        ? TelemetrySessionEventType.SourceUnavailable
                        : TelemetrySessionEventType.SourceStateChanged;
                    session.AddEvent(new TelemetrySessionEvent(
                        type,
                        at,
                        report.Source.ToString(),
                        null,
                        null,
                        previous.ToString(),
                        report.Status.ToString(),
                        report.Message));
                    _lastSourceStatus[report.Source] = report.Status;
                }
            }
        }

        private void RecordMetricSourceEvents(
            TelemetryRecordingSession session,
            IReadOnlyList<TelemetryReading> readings,
            DateTimeOffset at)
        {
            foreach (var reading in readings)
            {
                var key = (reading.Device.Kind, reading.Device.DeviceKey, reading.MetricKey.Value);
                if (!_lastMetricSource.TryGetValue(key, out var previous))
                {
                    _lastMetricSource[key] = reading.Source;
                    continue;
                }

                if (previous != reading.Source)
                {
                    session.AddEvent(new TelemetrySessionEvent(
                        TelemetrySessionEventType.MetricSourceChanged,
                        at,
                        reading.Source.ToString(),
                        reading.MetricKey.Value,
                        reading.Device.DeviceKey,
                        previous.ToString(),
                        reading.Source.ToString(),
                        reading.Device.DisplayName + " · " + reading.MetricKey.Value));
                    _lastMetricSource[key] = reading.Source;
                }
            }
        }
    }
}

