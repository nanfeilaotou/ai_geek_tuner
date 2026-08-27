using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Services.Voice;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.ViewModels
{
    public sealed class SessionListItem
    {
        public SessionListItem(string id, string startedText, string durationText, int sampleCount)
        {
            Id = id; StartedText = startedText; DurationText = durationText; SampleCount = sampleCount;
        }

        public string Id { get; }
        public string StartedText { get; }
        public string DurationText { get; }
        public int SampleCount { get; }
    }

    public sealed class LiveMetricRow
    {
        public LiveMetricRow(string label, string current, string min, string max)
            => (Label, Current, Min, Max) = (label, current, min, max);

        public string Label { get; }
        public string Current { get; }
        public string Min { get; }
        public string Max { get; }
    }

    public sealed class StatisticRow
    {
        public StatisticRow(string device, string metric, string unit, int samples,
            string coverage, string min, string avg, string max, string p95)
            => (Device, Metric, Unit, Samples, Coverage, Min, Avg, Max, P95) =
               (device, metric, unit, samples, coverage, min, avg, max, p95);

        public string Device { get; }
        public string Metric { get; }
        public string Unit { get; }
        public int Samples { get; }
        public string Coverage { get; }
        public string Min { get; }
        public string Avg { get; }
        public string Max { get; }
        public string P95 { get; }
    }

    public sealed class EventRow
    {
        public EventRow(string timeText, string headline, string detail)
            => (TimeText, Headline, Detail) = (timeText, headline, detail);

        public string TimeText { get; }
        public string Headline { get; }
        public string Detail { get; }
    }

    /// <summary>V2-M2 数据录制页 ViewModel：空态 / 录制中 / 摘要三态。</summary>
    public sealed class SessionsViewModel : ViewModelBase
    {
        private readonly ITelemetryRecordingService _recorder;
        private readonly ITelemetrySessionStore _store;
        private readonly IApplicationSettingsService _settingsService;

        private bool _isRecording;
        private bool _hasResult;
        private bool _hasError;
        private string _errorText = string.Empty;
        private string _elapsedText = "00:00:00";
        private string _samplesText = "0";
        private string _intervalText = "2 s";
        private SessionListItem? _selectedRecent;

        public SessionsViewModel(
            ITelemetryRecordingService recorder,
            ITelemetrySessionStore store,
            IApplicationSettingsService settingsService,
            ISessionAnalysisService analysisService,
            ISessionAnalysisStore analysisStore,
            IVoiceSynthesisService voiceService,
            IWavPlaybackService wavPlayback,
            Func<VoiceConfiguration> voiceSnapshot)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
            _analysisStore = analysisStore ?? throw new ArgumentNullException(nameof(analysisStore));
            _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
            _wavPlayback = wavPlayback ?? throw new ArgumentNullException(nameof(wavPlayback));
            _voiceSnapshot = voiceSnapshot ?? throw new ArgumentNullException(nameof(voiceSnapshot));

            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsAnalyzing);
            PlaySpokenSummaryCommand = new RelayCommand(PlaySpokenSummary, () =>
                !IsRecording && HasAnalysis && !string.IsNullOrWhiteSpace(SpokenSummary)
                && VoiceState is VoicePlaybackState.Idle or VoicePlaybackState.Error);

            StartRecordingCommand = new RelayCommand(StartRecording, () => !IsRecording);
            StopAndAnalyzeCommand = new AsyncRelayCommand(StopAndAnalyzeAsync, () => IsRecording);
            SelectRecentCommand = new RelayCommand(SelectRecent, () => SelectedRecent is not null);
            DeleteRecentCommand = new RelayCommand(DeleteRecent, () => SelectedRecent is not null);

            // 录制独立于页面生命周期：构造时若已有活动会话（切页回来）直接恢复视图。
            SyncFromRecorder();
            LoadRecent();
        }

        public Func<string, bool>? ConfirmDelete { get; set; }

        public ObservableCollection<SessionListItem> RecentSessions { get; } = [];
        public ObservableCollection<LiveMetricRow> LiveMetrics { get; } = [];
        public ObservableCollection<StatisticRow> Statistics { get; } = [];
        public ObservableCollection<EventRow> Events { get; } = [];

        public SessionListItem? SelectedRecent { get => _selectedRecent; set => SetProperty(ref _selectedRecent, value); }

        public bool IsRecording { get => _isRecording; private set { if (SetProperty(ref _isRecording, value)) OnPropertyChanged(nameof(HasEmptyState)); } }
        public bool HasResult { get => _hasResult; private set { if (SetProperty(ref _hasResult, value)) OnPropertyChanged(nameof(HasEmptyState)); } }
        public bool HasEmptyState => !IsRecording && !HasResult;
        public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
        public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
        public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
        public string SamplesText { get => _samplesText; private set => SetProperty(ref _samplesText, value); }
        public string IntervalText { get => _intervalText; private set => SetProperty(ref _intervalText, value); }

        public RelayCommand StartRecordingCommand { get; }
        public AsyncRelayCommand StopAndAnalyzeCommand { get; }
        public RelayCommand SelectRecentCommand { get; }
        public RelayCommand DeleteRecentCommand { get; }

        // ---- V2-M3：AI 分析 ----
        private readonly ISessionAnalysisService _analysisService;
        private readonly ISessionAnalysisStore _analysisStore;
        private bool _isAnalyzing;
        private bool _hasAnalysis;
        private string _assessmentBadge = string.Empty;
        private string _confidenceText = string.Empty;
        private string _analysisSummary = string.Empty;
        private string _analysisError = string.Empty;
        private string _analysisDurationText = string.Empty;

        public ObservableCollection<AnalysisFindingRow> Findings { get; } = [];
        public ObservableCollection<string> Recommendations { get; } = [];
        public ObservableCollection<string> Uncertainties { get; } = [];

        public AsyncRelayCommand AnalyzeCommand { get; }

        public bool IsAnalyzing { get => _isAnalyzing; private set => SetProperty(ref _isAnalyzing, value); }
        public bool HasAnalysis { get => _hasAnalysis; private set => SetProperty(ref _hasAnalysis, value); }
        public string AssessmentBadge { get => _assessmentBadge; private set => SetProperty(ref _assessmentBadge, value); }
        public string ConfidenceText { get => _confidenceText; private set => SetProperty(ref _confidenceText, value); }
        public string AnalysisSummary { get => _analysisSummary; private set => SetProperty(ref _analysisSummary, value); }
        public string AnalysisError { get => _analysisError; private set => SetProperty(ref _analysisError, value); }
        public string AnalysisDurationText { get => _analysisDurationText; private set => SetProperty(ref _analysisDurationText, value); }

        // ---- V2-M3：语音摘要 ----
        private readonly IVoiceSynthesisService _voiceService;
        private readonly IWavPlaybackService _wavPlayback;
        private readonly Func<VoiceConfiguration> _voiceSnapshot;
        private VoicePlaybackState _voiceState = VoicePlaybackState.Idle;
        private string _spokenSummary = string.Empty;
        private string _voiceStateText = "未生成";

        public RelayCommand PlaySpokenSummaryCommand { get; }

        public string SpokenSummary { get => _spokenSummary; private set => SetProperty(ref _spokenSummary, value); }
        public string VoiceStateText { get => _voiceStateText; private set => SetProperty(ref _voiceStateText, value); }

        public VoicePlaybackState VoiceState { get => _voiceState; private set
            {
                if (SetProperty(ref _voiceState, value))
                {
                    VoiceStateText = value switch
                    {
                        VoicePlaybackState.Generating => "正在生成语音…",
                        VoicePlaybackState.Playing => "播放中…",
                        VoicePlaybackState.Error => "语音服务出错",
                        _ => "未生成",
                    };
                    PlaySpokenSummaryCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public enum VoicePlaybackState { Idle, Generating, Playing, Error }


        public void StartRecording()
        {
            var interval = _settingsService.Current.RecordingIntervalMs;
            ErrorText = string.Empty;
            HasError = false;
            if (!_recorder.Start(interval))
            {
                HasError = true;
                ErrorText = "无法开始录制：可能已有会话进行中，或采样间隔非法。";
                return;
            }

            HasResult = false;
            Statistics.Clear();
            Events.Clear();
            SyncFromRecorder();
            RefreshLive();
        }

        public async Task StopAndAnalyzeAsync()
        {
            var session = await _recorder.StopAsync().ConfigureAwait(true);
            if (session is null)
            {
                SyncFromRecorder();
                return;
            }

            if (_recorder.LastSavedPath is null && _recorder.LastError is not null)
            {
                HasError = true;
                ErrorText = _recorder.LastError;
            }

            BuildSummary(session);
            SyncFromRecorder();
            LoadRecent();
        }

        /// <summary>由页面定时器驱动；只读最新状态并更新少量行（§45）。</summary>
        public void RefreshLive()
        {
            if (!IsRecording)
            {
                return;
            }

            SyncFromRecorder();
            var session = _recorder.CurrentSession;
            if (session is null)
            {
                return;
            }

            ElapsedText = FormatDuration(DateTimeOffset.UtcNow - session.StartedAtUtc);
            SamplesText = session.Samples.Count.ToString("N0");
            IntervalText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{session.RequestedIntervalMs / 1000d:0.#} s");

            var aggregate = new Dictionary<string, (double Cur, double Min, double Max, TelemetryUnit Unit)>(StringComparer.Ordinal);
            foreach (var sample in session.Samples)
            {
                foreach (var reading in sample.Readings)
                {
                    var label = LabelOf(reading);
                    if (!aggregate.TryGetValue(label, out var entry))
                    {
                        aggregate[label] = (reading.Value, reading.Value, reading.Value, reading.Unit);
                    }
                    else
                    {
                        aggregate[label] = (reading.Value, Math.Min(entry.Min, reading.Value), Math.Max(entry.Max, reading.Value), reading.Unit);
                    }
                }
            }

            LiveMetrics.Clear();
            foreach (var kvp in aggregate)
            {
                LiveMetrics.Add(new LiveMetricRow(
                    kvp.Key,
                    TelemetrySessionAnalyzer.FormatValue(kvp.Value.Cur, kvp.Value.Unit),
                    TelemetrySessionAnalyzer.FormatValue(kvp.Value.Min, kvp.Value.Unit),
                    TelemetrySessionAnalyzer.FormatValue(kvp.Value.Max, kvp.Value.Unit)));
            }
        }

        public void LoadRecent()
        {
            var sessions = _store.LoadAll(out _);
            RecentSessions.Clear();
            foreach (var session in sessions.Take(20))
            {
                RecentSessions.Add(new SessionListItem(
                    session.Id,
                    session.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    FormatDuration((session.CompletedAtUtc ?? session.StartedAtUtc) - session.StartedAtUtc),
                    session.Samples.Count));
            }
        }

        private void SelectRecent()
        {
            if (SelectedRecent is null || IsRecording)
            {
                return;
            }

            var full = _store.Load(SelectedRecent.Id);
            if (full is null)
            {
                HasError = true;
                ErrorText = "会话文件不存在或已损坏。";
                return;
            }

            if (full.Summary is null)
            {
                full = full with { Summary = TelemetrySessionAnalyzer.Analyze(full) };
            }

            BuildSummary(full);
            HasResult = true;
        }

        private void DeleteRecent()
        {
            if (SelectedRecent is null)
            {
                return;
            }

            var id = SelectedRecent.Id;
            if (!(ConfirmDelete?.Invoke(id) ?? true))
            {
                return;
            }

            _store.Delete(id);
            SelectedRecent = null;
            LoadRecent();
        }

        private void BuildSummary(TelemetryRecordingSession session)
        {
            var summary = session.Summary ?? TelemetrySessionAnalyzer.Analyze(session);
            Statistics.Clear();
            foreach (var statistic in summary.Statistics)
            {
                Statistics.Add(new StatisticRow(
                    statistic.DeviceName,
                    MetricLabel(statistic.MetricKey),
                    statistic.Unit,
                    statistic.SampleCount,
                    statistic.CoveragePercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%",
                    TelemetrySessionAnalyzer.FormatValue(statistic.Minimum, ParseUnit(statistic.Unit)),
                    TelemetrySessionAnalyzer.FormatValue(statistic.Average, ParseUnit(statistic.Unit)),
                    TelemetrySessionAnalyzer.FormatValue(statistic.Maximum, ParseUnit(statistic.Unit)),
                    TelemetrySessionAnalyzer.FormatValue(statistic.P95, ParseUnit(statistic.Unit))));
            }

            Events.Clear();
            foreach (var @event in summary.TopEvents)
            {
                var head = @event.Type switch
                {
                    TelemetrySessionEventType.ThrottleObserved => "CPU Throttle",
                    TelemetrySessionEventType.SourceStateChanged or
                    TelemetrySessionEventType.SourceUnavailable => @event.Source ?? "来源",
                    TelemetrySessionEventType.MetricSourceChanged => @event.DeviceKey + " · " + @event.MetricKey + " 来源",
                    _ => string.IsNullOrEmpty(@event.MetricKey) ? @event.Detail : @event.MetricKey,
                };
                var change = string.IsNullOrEmpty(@event.From)
                    ? string.Empty
                    : "  " + @event.From + " → " + @event.To;
                Events.Add(new EventRow(
                    @event.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                    head + change,
                    @event.Detail));
            }

            HasResult = true;
        }

        public async Task AnalyzeAsync()
        {
            if (IsAnalyzing || SelectedRecent is null)
            {
                return;
            }

            var session = _store.Load(SelectedRecent.Id);
            if (session is null)
            {
                AnalysisError = "会话不存在，无法分析。";
                return;
            }

            IsAnalyzing = true;
            AnalysisError = string.Empty;
            try
            {
                var summary = session.Summary ?? TelemetrySessionAnalyzer.Analyze(session);
                var context = TelemetrySessionAnalyzer.BuildAnalysisContext(
                    session with { Summary = summary });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var run = await _analysisService.AnalyzeAsync(context, CancellationToken.None);
                sw.Stop();

                if (!run.Success || run.Result is null)
                {
                    AnalysisError = $"AI 分析失败：{run.ErrorMessage}（请求 {run.RequestCount} 次）";
                    return;
                }

                ApplyAnalysis(run.Result, run.ModelName, sw.ElapsedMilliseconds,
                    JsonSerializer.Serialize(context), session.Id);
            }
            catch (OperationCanceledException)
            {
                // 用户取消：保留旧分析。
            }
            catch (Exception exception)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(exception, "Sessions analyze");
                AnalysisError = "AI 分析失败：" + exception.Message;
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        /// <summary>把 AI 结果渲染到界面；证据 ID 映射为可读文本（§30）。</summary>
        private void ApplyAnalysis(
            SessionAnalysisResult result,
            string modelName,
            long durationMs,
            string contextJson,
            string sessionId)
        {
            AssessmentBadge = result.OverallAssessment.ToString();
            ConfidenceText = $"Confidence {result.Confidence * 100:0}%";
            AnalysisSummary = result.Summary;
            AnalysisDurationText = $"耗时 {durationMs} ms · 模型 {modelName}";

            Findings.Clear();
            foreach (var finding in result.Findings)
            {
                var evidenceText = finding.EvidenceIds.Count == 0
                    ? string.Empty
                    : "  依据: " + string.Join("、", finding.EvidenceIds.Select(DescribeEvidence));
                Findings.Add(new AnalysisFindingRow(
                    finding.Title,
                    finding.Category.ToString(),
                    finding.Assessment,
                    finding.Explanation + evidenceText));
            }

            Recommendations.Clear();
            foreach (var recommendation in result.Recommendations)
            {
                Recommendations.Add(recommendation.Text);
            }

            Uncertainties.Clear();
            foreach (var uncertainty in result.Uncertainties)
            {
                Uncertainties.Add(uncertainty);
            }

            SpokenSummary = result.SpokenSummary;
            HasAnalysis = true;
            VoiceState = VoicePlaybackState.Idle;
            void ApplyLocal() { }
            ApplyLocal();
            _ = modelName; _ = durationMs; _ = contextJson; _ = sessionId; // 元数据由 store 层持久化
        }

        internal static string DescribeEvidence(string evidenceId)
        {
            if (evidenceId.StartsWith("stat:", StringComparison.Ordinal))
            {
                var parts = evidenceId.Split(':');
                var metric = parts.Length > 2 ? parts[^1] : evidenceId;
                return MetricLabel(metric) + " 统计";
            }

            if (evidenceId.StartsWith("event:", StringComparison.Ordinal))
            {
                return "关键事件 " + evidenceId["event:".Length..].TrimStart('0');
            }

            return evidenceId;
        }

        public void PlaySpokenSummary()
        {
            if (VoiceState is VoicePlaybackState.Generating or VoicePlaybackState.Playing
                || string.IsNullOrWhiteSpace(SpokenSummary))
            {
                return;
            }

            var text = SpokenSummary;
            var configuration = _voiceSnapshot();
            VoiceState = VoicePlaybackState.Generating;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _voiceService.SynthesizeAsync(text, configuration, CancellationToken.None);
                    if (!result.Succeeded || result.WavBytes is null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            VoiceState = VoicePlaybackState.Error;
                            ErrorText = result.ErrorMessage ?? "语音合成失败。";
                            HasError = true;
                        });
                        return;
                    }

                    var cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AI-GeekTuner", "VoiceCache");
                    Directory.CreateDirectory(cacheDir);
                    var wavPath = Path.Combine(cacheDir, "spoken-summary.wav");
                    File.WriteAllBytes(wavPath, result.WavBytes);

                    await Application.Current.Dispatcher.InvokeAsync(() => VoiceState = VoicePlaybackState.Playing);
                    _wavPlayback.PlayWav(result.WavBytes);
                    await Application.Current.Dispatcher.InvokeAsync(() => VoiceState = VoicePlaybackState.Idle);
                }
                catch (Exception exception)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        VoiceState = VoicePlaybackState.Error;
                        ErrorText = exception.Message;
                        HasError = true;
                    });
                }
            });
        }

        private void SyncFromRecorder()
        {
            var recording = _recorder.IsRecording;
            if (recording != IsRecording)
            {
                IsRecording = recording;
            }

            StartRecordingCommand.NotifyCanExecuteChanged();
            StopAndAnalyzeCommand.NotifyCanExecuteChanged();
        }

        internal static string LabelOf(TelemetryReading reading)
        {
            var device = reading.Device.Kind switch
            {
                TelemetryDeviceKind.Cpu => "CPU",
                TelemetryDeviceKind.Gpu => reading.Device.DisplayName.Length > 0
                    ? "GPU · " + reading.Device.DisplayName
                    : "GPU",
                TelemetryDeviceKind.Memory => "内存",
                TelemetryDeviceKind.Storage => "磁盘 · " + reading.Device.DisplayName,
                _ => reading.Device.DisplayName,
            };
            var metric = reading.MetricKey.Value switch
            {
                "cpu.package.temperature" or "gpu.core.temperature" or "storage.temperature" => "温度",
                "gpu.hotspot.temperature" => "热点温度",
                "gpu.memory.temperature" => "显存温度",
                "cpu.total.utilization" or "gpu.core.utilization" or "memory.utilization" => "使用率",
                "cpu.clock" or "gpu.core.clock" or "memory.clock" => "频率",
                "cpu.package.power" or "gpu.board.power" => "功耗",
                "cpu.throttling" => "降频占比",
                "memory.used" or "gpu.memory.used" => "已用容量",
                _ => reading.MetricKey.Value,
            };
            return device + " " + metric;
        }

        private static TelemetryUnit ParseUnit(string unit) => unit switch
        {
            "Celsius" => TelemetryUnit.Celsius,
            "Watt" => TelemetryUnit.Watt,
            "Megahertz" => TelemetryUnit.Megahertz,
            "Percent" => TelemetryUnit.Percent,
            "Volt" => TelemetryUnit.Volt,
            "Byte" => TelemetryUnit.Byte,
            _ => TelemetryUnit.None,
        };

        private static string MetricLabel(string metricKey) => metricKey switch
        {
            "cpu.package.temperature" => "温度",
            "cpu.package.power" => "Package Power",
            "cpu.total.utilization" => "使用率",
            "cpu.clock" => "时钟频率",
            "cpu.throttling" => "降频占比",
            "gpu.core.temperature" => "温度",
            "gpu.hotspot.temperature" => "热点温度",
            "gpu.memory.temperature" => "显存温度",
            "gpu.board.power" => "功耗",
            "gpu.core.utilization" => "使用率",
            "gpu.core.clock" => "核心频率",
            "gpu.memory.used" => "已用显存",
            "memory.used" => "已用内存",
            "memory.utilization" => "使用率",
            "memory.clock" => "内存频率",
            "storage.temperature" => "温度",
            _ => metricKey,
        };

        private static string FormatDuration(TimeSpan duration)
            => (int)duration.TotalHours > 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{duration.Minutes:00}:{duration.Seconds:00}");
    }
}



