using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using System.Windows.Input;
using System.ComponentModel;
using System.Globalization;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Services.Voice;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.ViewModels
{
    public sealed class SessionListItem
    {
        public SessionListItem(string id, string startedText, string durationText, int sampleCount, bool isAnalyzed)
        {
            Id = id; StartedText = startedText; DurationText = durationText; SampleCount = sampleCount;
            IsAnalyzed = isAnalyzed;
        }

        public string Id { get; }
        public string StartedText { get; }
        public string DurationText { get; }
        public int SampleCount { get; }

        /// <summary>M4.5E.1 补充：analysis.json 是否存在（录制多时一眼分清哪个有 AI 结果）。</summary>
        public bool IsAnalyzed { get; }

        /// <summary>历史行状态后缀：完成状态标记，不是健康判断。</summary>
        public string AnalysisStateText => IsAnalyzed ? "已分析" : "未分析";

        /// <summary>UIA/屏幕阅读器行名回退到 ToString——给出可读行文本而非类型名。</summary>
        public override string ToString() =>
            StartedText + "  " + DurationText + "  " + SampleCount + " samples  " + AnalysisStateText;
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

    /// <summary>
    /// Windows 事件证据的极小展示行（V2-M4.4 Gate B）：
    /// 不建立新业务 Model、不修改 WindowsIncident。EvidenceId 不作为主 UI 文本，
    /// 仅用于 Tooltip 与 AI evidence mapping。Severity 是 Windows Event Level，
    /// 不是对硬件健康的判断——只用一个小圆点表达，绝不说“危险/严重故障”。
    /// </summary>
    public sealed class SessionIncidentRow : INotifyPropertyChanged
    {
        private bool _isInAiContext;

        public SessionIncidentRow(WindowsIncident incident, bool isInAiContext)
        {
            EvidenceId = incident.EvidenceId;
            OccurredAtLocal = incident.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            CategoryDisplay = CategoryDisplayName(incident.Category);
            Severity = incident.Severity;
            Summary = incident.Summary;
            ProviderEventText = incident.ProviderName + " · Event " + incident.EventId;
            RowToolTip = incident.ProviderName + " · Event " + incident.EventId
                + "\nChannel: " + incident.Channel
                + " · RecordId: " + (incident.RecordId?.ToString(CultureInfo.InvariantCulture) ?? "-")
                + "\n" + incident.Summary
                + "\nEvidenceId: " + incident.EvidenceId;
            SeverityBrush = SeverityBrushOf(incident.Severity);
            SeverityToolTip = "Windows 事件级别：" + SeverityLevelLabel(incident.Severity);
            _isInAiContext = isInAiContext;
        }

        public string EvidenceId { get; }
        public string OccurredAtLocal { get; }
        public string CategoryDisplay { get; }
        public IncidentSeverity Severity { get; }
        public string Summary { get; }
        public string ProviderEventText { get; }
        public string RowToolTip { get; }
        public System.Windows.Media.Brush SeverityBrush { get; }
        public string SeverityToolTip { get; }

        public bool IsInAiContext => _isInAiContext;
        public bool HasAiMarker => _isInAiContext;
        public string AiMarkerText => _isInAiContext ? "AI 分析上下文" : string.Empty;

        /// <summary>Gate H：只标记真正进入 AI 分析上下文的 incident（reducer omitted 的不标）。</summary>
        public void SetAiContext(bool isInAiContext)
        {
            if (_isInAiContext == isInAiContext)
            {
                return;
            }

            _isInAiContext = isInAiContext;
            OnPropertyChanged(nameof(IsInAiContext));
            OnPropertyChanged(nameof(HasAiMarker));
            OnPropertyChanged(nameof(AiMarkerText));
        }

        // Gate E：用户可见类别名，不显示 enum 原名。
        public static string CategoryDisplayName(IncidentCategory category) => category switch
        {
            IncidentCategory.UnexpectedShutdown => "非正常关机",
            IncidentCategory.BugCheck => "蓝屏 / BugCheck",
            IncidentCategory.HardwareError => "硬件错误",
            IncidentCategory.DisplayDriver => "显示驱动事件",
            IncidentCategory.Storage => "存储事件",
            IncidentCategory.ApplicationCrash => "应用崩溃",
            IncidentCategory.ApplicationHang => "应用无响应",
            IncidentCategory.WindowsErrorReporting => "Windows 错误报告",
            _ => "其他事件",
        };

        // Gate F：只呈现 Windows Event Level 事实，不做健康判断。
        public static string SeverityLevelLabel(IncidentSeverity severity) => severity switch
        {
            IncidentSeverity.Critical => "Critical",
            IncidentSeverity.Error => "Error",
            IncidentSeverity.Warning => "Warning",
            _ => "Information",
        };

        private static System.Windows.Media.Brush SeverityBrushOf(IncidentSeverity severity) => severity switch
        {
            IncidentSeverity.Critical => Frozen("#FF6B6B"),
            IncidentSeverity.Error => Frozen("#FF9A62"),
            IncidentSeverity.Warning => Frozen("#FFE08A"),
            _ => Frozen("#9FD9EA"),
        };

        private static System.Windows.Media.Brush Frozen(string hex)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>V2-M2 数据录制页 ViewModel：空态 / 录制中 / 摘要三态。</summary>
    public sealed class SessionsViewModel : ViewModelBase
    {
        private readonly ITelemetryRecordingService _recorder;
        private readonly ITelemetrySessionStore _store;
        private readonly IApplicationSettingsService _settingsService;
        private readonly ISessionIncidentCorrelationService _incidentCorrelation;
        private readonly ISessionIncidentStore _incidentStore;

        private bool _isRecording;
        private bool _hasResult;
        private bool _hasError;
        private string _errorText = string.Empty;
        private string _elapsedText = "00:00:00";
        private string _samplesText = "0";
        private string _intervalText = "2 s";
        private SessionListItem? _selectedRecent;

        // ---- V2-M4.5E：页面内部三状态 presentation（Gate B）----
        /// <summary>
        /// 同一 SessionsPage 内部的 presentation 状态，不是 App 级导航、不新增 AppPage：
        /// Record = 开始/进行中录制视图；History = 录制历史浏览；Detail = 已载入会话详情。
        /// ViewModel 随 MainWindow 单例存活——切页离开再回来，模式天然保留（Gate H）。
        /// </summary>
        public enum SessionPageMode { Record, History, Detail }

        private SessionPageMode _pageMode = SessionPageMode.Record;

        public SessionPageMode PageMode
        {
            get => _pageMode;
            private set
            {
                if (SetProperty(ref _pageMode, value))
                {
                    OnPropertyChanged(nameof(IsRecordMode));
                    OnPropertyChanged(nameof(IsHistoryMode));
                    OnPropertyChanged(nameof(IsDetailMode));
                }
            }
        }

        public bool IsRecordMode => PageMode == SessionPageMode.Record;
        public bool IsHistoryMode => PageMode == SessionPageMode.History;
        public bool IsDetailMode => PageMode == SessionPageMode.Detail;

        public SessionsViewModel(
            ITelemetryRecordingService recorder,
            ITelemetrySessionStore store,
            IApplicationSettingsService settingsService,
            ISessionAnalysisService analysisService,
            ISessionAnalysisStore analysisStore,
            IVoiceSynthesisService voiceService,
            IWavPlaybackService wavPlayback,
            Func<VoiceConfiguration> voiceSnapshot,
            ISessionIncidentCorrelationService incidentCorrelation,
            ISessionIncidentStore incidentStore)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
            _analysisStore = analysisStore ?? throw new ArgumentNullException(nameof(analysisStore));
            _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
            _wavPlayback = wavPlayback ?? throw new ArgumentNullException(nameof(wavPlayback));
            _voiceSnapshot = voiceSnapshot ?? throw new ArgumentNullException(nameof(voiceSnapshot));
            _incidentCorrelation = incidentCorrelation ?? throw new ArgumentNullException(nameof(incidentCorrelation));
            _incidentStore = incidentStore ?? throw new ArgumentNullException(nameof(incidentStore));

            // M4.5E.3 Gate E：app 在 UI 线程构造 ViewModel → 捕获 Dispatcher 同步上下文；
            // 单元测试/无 WPF 宿主下可能为 null → UI 回退为内联执行（RunOnUiThread）。
            _uiSynchronizationContext = SynchronizationContext.Current;
            _uiThreadId = Environment.CurrentManagedThreadId;

            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync,
                () => !IsAnalyzing && CurrentDetailSessionId is not null);
            PlaySpokenSummaryCommand = new RelayCommand(PlaySpokenSummary, () =>
                !IsRecording && HasAnalysis && !string.IsNullOrWhiteSpace(SpokenSummary)
                && VoiceState is VoicePlaybackState.Idle or VoicePlaybackState.Error);

            StartRecordingCommand = new RelayCommand(StartRecording, () => !IsRecording);
            StopAndAnalyzeCommand = new AsyncRelayCommand(StopAndAnalyzeAsync, () => IsRecording);
            SelectRecentCommand = new RelayCommand(SelectRecent, () => SelectedRecent is not null);
            DeleteRecentCommand = new RelayCommand(DeleteRecent, () => SelectedRecent is not null);

            // V2-M4.5E Gate C：头部常驻操作。OpenRecordView 只切视图——录制中语义是
            // “返回当前录制”（按钮文案随 IsRecording 切换），绝不停录、绝不二次启动。
            OpenRecordViewCommand = new RelayCommand(OpenRecordView);
            OpenHistoryViewCommand = new RelayCommand(OpenHistoryView);

            // 录制独立于页面生命周期：构造时若已有活动会话（切页回来）直接恢复视图。
            SyncFromRecorder();
            LoadRecent();
        }

        public Func<string, bool>? ConfirmDelete { get; set; }

        public ObservableCollection<SessionListItem> RecentSessions { get; } = [];
        public ObservableCollection<LiveMetricRow> LiveMetrics { get; } = [];
        public ObservableCollection<StatisticRow> Statistics { get; } = [];
        public ObservableCollection<EventRow> Events { get; } = [];

        public SessionListItem? SelectedRecent
        {
            get => _selectedRecent;
            set
            {
                if (SetProperty(ref _selectedRecent, value))
                {
                    // V2-M4.5D Gate Q 回归修复：选中状态变化必须刷新依赖它的命令，
                    // 否则分析按钮会以“可用”外观静默 no-op（AnalyzeAsync 直接 return）。
                    AnalyzeCommand.NotifyCanExecuteChanged();
                    SelectRecentCommand.NotifyCanExecuteChanged();
                    DeleteRecentCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsRecording
        {
            get => _isRecording;
            private set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(HasEmptyState));
                    // V2-M4.5E Gate C/I：录制中头部按钮文案“新建录制”↔“返回当前录制”。
                    OnPropertyChanged(nameof(NewRecordingButtonText));
                }
            }
        }
        public bool HasResult { get => _hasResult; private set { if (SetProperty(ref _hasResult, value)) OnPropertyChanged(nameof(HasEmptyState)); } }

        /// <summary>V2-M4.5E：Record 视图内“开始录制入口”的可见性（不再依赖 HasResult——那是 Detail 的状态）。</summary>
        public bool HasEmptyState => !IsRecording;

        /// <summary>Gate C：历史视图空态（无任何已保存会话）。</summary>
        public bool HasNoHistory => RecentSessions.Count == 0;

        /// <summary>Gate I：录制已存在时，页头“新建录制”语义变为“返回当前录制”（唯一导航入口）。</summary>
        public string NewRecordingButtonText => IsRecording ? "返回当前录制" : "新建录制";
        public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
        public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
        public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
        public string SamplesText { get => _samplesText; private set => SetProperty(ref _samplesText, value); }
        public string IntervalText { get => _intervalText; private set => SetProperty(ref _intervalText, value); }

        public RelayCommand StartRecordingCommand { get; }
        public AsyncRelayCommand StopAndAnalyzeCommand { get; }
        public RelayCommand SelectRecentCommand { get; }
        public RelayCommand DeleteRecentCommand { get; }
        public RelayCommand OpenRecordViewCommand { get; }
        public RelayCommand OpenHistoryViewCommand { get; }

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

        // M4.5E.2 Gate C：进行中的分析操作属于发起时的目标会话。
        // 当前只允许一个并发分析（最小设计，不造 TaskRegistry）。
        private string? _activeAnalysisSessionId;

        public ObservableCollection<AnalysisFindingRow> Findings { get; } = [];
        public ObservableCollection<string> Recommendations { get; } = [];
        public ObservableCollection<string> Uncertainties { get; } = [];

        public AsyncRelayCommand AnalyzeCommand { get; }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            private set
            {
                if (SetProperty(ref _isAnalyzing, value))
                {
                    AnalyzeCommand.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// M4.5E.2 Gate C：分析中的可见性按会话隔离——只有 Detail 正在展示
        /// 正被分析的那个会话时才为 true。全局 IsAnalyzing 只作并发守卫。
        /// </summary>
        public bool IsCurrentDetailAnalyzing =>
            _activeAnalysisSessionId is not null && _activeAnalysisSessionId == CurrentDetailSessionId;
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
        private string? _currentSessionId;

        // M4.5E.2 Gate J：进行中的语音操作（生成/播放）属于发起时的目标会话，
        // 只影响该会话的显示；用户切到其它会话时绝不污染。
        private string? _activeVoiceSessionId;

        // M4.5E.3 Gate E：ViewModel 在组合根（UI 线程）构造——捕获该线程的同步上下文，
        // 后台语音回调的唯一 UI 状态回写通道。绝不依赖 Application.Current（曾因并行
        // 测试中的 foreign Application 延迟回调导致状态滞留，禁止改回）。
        private readonly SynchronizationContext? _uiSynchronizationContext;

        /// <summary>构造线程（app = UI 线程）的 ManagedThreadId；已在 UI 线程时内联执行。</summary>
        private readonly int _uiThreadId;

        // M4.5E.3 Gate F：当前 Error 态的用户可读简短文案（播放失败 / 预生成失败）。
        // 技术细节留在 ExceptionLogWriter 与 ErrorText，UI 只展示短句。
        private string? _voiceFailureText;

        /// <summary>
        /// M4.5E.1 Gate A/B：Detail 当前展示的会话 Id。与 SelectedRecent（History 列表选择）
        /// 是两个独立概念，绝不让一个属性同时承担——stop 后列表选择可以仍是旧 A，Detail 必须是 B。
        /// </summary>
        public string? CurrentDetailSessionId => _currentSessionId;

        /// <summary>更新 Detail 展示会话并刷新依赖它的命令（分析/播放/状态显示都以 Detail 会话为准）。</summary>
        private void SetCurrentDetailSession(string? sessionId)
        {
            if (_currentSessionId == sessionId)
            {
                return;
            }

            _currentSessionId = sessionId;
            AnalyzeCommand.NotifyCanExecuteChanged();
            PlaySpokenSummaryCommand.NotifyCanExecuteChanged();
            // M4.5E.2：切换 Detail 会话后，分析/语音的 per-session 显示状态全部重算。
            OnPropertyChanged(nameof(IsCurrentDetailAnalyzing));
            OnPropertyChanged(nameof(IsCurrentDetailVoiceBusy));
            OnPropertyChanged(nameof(VoiceStateText));
        }

        public RelayCommand PlaySpokenSummaryCommand { get; }

        // ---- V2-M4.4：Windows 事件证据展示（只读 incidents.json，不重新查询） ----
        /// <summary>UI display cap：只影响展示，不修改 incidents.json、不影响 AI reducer（Gate G）。</summary>
        public const int IncidentDisplayCap = 50;

        private readonly Dictionary<string, string> _incidentEvidenceLookup = new(StringComparer.Ordinal);
        private string _incidentHeaderText = "Windows 事件证据";
        private string _incidentStateText = string.Empty;
        private string _incidentQualityText = string.Empty;
        private bool _hasIncidentQualityText;
        private string _incidentOverflowText = string.Empty;
        private bool _hasIncidentRows;
        private bool _hasIncidentStateText;
        private bool _hasIncidentOverflow;

        public ObservableCollection<SessionIncidentRow> IncidentRows { get; } = [];

        public string IncidentHeaderText { get => _incidentHeaderText; private set => SetProperty(ref _incidentHeaderText, value); }
        public string IncidentStateText { get => _incidentStateText; private set => SetProperty(ref _incidentStateText, value); }
        public string IncidentQualityText { get => _incidentQualityText; private set => SetProperty(ref _incidentQualityText, value); }
        public bool HasIncidentQualityText { get => _hasIncidentQualityText; private set => SetProperty(ref _hasIncidentQualityText, value); }
        public string IncidentOverflowText { get => _incidentOverflowText; private set => SetProperty(ref _incidentOverflowText, value); }
        public bool HasIncidentRows { get => _hasIncidentRows; private set => SetProperty(ref _hasIncidentRows, value); }
        public bool HasIncidentStateText { get => _hasIncidentStateText; private set => SetProperty(ref _hasIncidentStateText, value); }
        public bool HasIncidentOverflow { get => _hasIncidentOverflow; private set => SetProperty(ref _hasIncidentOverflow, value); }

        public string SpokenSummary
        {
            get => _spokenSummary;
            private set
            {
                if (SetProperty(ref _spokenSummary, value))
                {
                    OnPropertyChanged(nameof(VoiceStateText));
                }
            }
        }

        /// <summary>
        /// M4.5E.2 Gate G/J：语音状态文本是<strong>计算属性</strong>，按“当前 Detail 会话”实时重算——
        /// 操作中（生成/播放）只对发起会话可见；缓存以磁盘事实（HasCachedVoice）为准，
        /// analysis 存在 ≠ 语音存在。重启后第一次 ShowDetail 即可正确显示“已生成”。
        /// </summary>
        public string VoiceStateText
        {
            get
            {
                if (IsCurrentDetailVoiceBusy)
                {
                    // 忙即“进行中”：生成（或操作状态被会话切换重置后回到生成会话）都显示生成中。
                    return _voiceState == VoicePlaybackState.Playing ? "播放中…" : "正在生成语音…";
                }

                if (string.IsNullOrWhiteSpace(SpokenSummary))
                {
                    return "未生成";
                }

                // M4.5E.3 Gate F：失败态先于缓存事实——否则“播放失败”会被“已生成”掩盖
                //（用户点击播放 → 失败 → 界面毫无反应的可见性缺口）。
                if (_voiceState == VoicePlaybackState.Error)
                {
                    return _voiceFailureText ?? "语音预生成失败，播放时会重试";
                }

                if (CurrentDetailSessionId is not null && _analysisStore.HasCachedVoice(CurrentDetailSessionId))
                {
                    return "语音已生成 · 播放将直接使用缓存";
                }

                return "未生成";
            }
        }

        /// <summary>M4.5E.2 Gate J：语音操作忙只对发起会话可见。</summary>
        public bool IsCurrentDetailVoiceBusy =>
            _activeVoiceSessionId is not null && _activeVoiceSessionId == CurrentDetailSessionId;

        public VoicePlaybackState VoiceState { get => _voiceState; private set
            {
                if (SetProperty(ref _voiceState, value))
                {
                    OnPropertyChanged(nameof(VoiceStateText));
                    PlaySpokenSummaryCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public enum VoicePlaybackState { Idle, Generating, Playing, Error }


        /// <summary>
        /// Gate C：头部“新建录制”。只切换到 Record 视图、不开始采样——开始始终由用户
        /// 点击“开始录制”触发。录制中该按钮文案为“返回当前录制”（Gate I），
        /// 绝不停掉进行中的会话、也绝不启动第二个录制。
        /// </summary>
        private void OpenRecordView()
        {
            PageMode = SessionPageMode.Record;
        }

        /// <summary>
        /// Gate E：History 是独立可进入视图（无需先录制）。只读已保存会话清单，
        /// 不重新查询 EventLog、不重新分析 AI、不触碰 recorder（录制在后台继续）。
        /// Detail 顶部的“← 返回历史”复用同一命令。
        /// </summary>
        private void OpenHistoryView()
        {
            LoadRecent();
            PageMode = SessionPageMode.History;
        }

        /// <summary>
        /// Gate H：从其它页面返回 Sessions 的入口规则（SessionsPage.Loaded 调用）。
        /// 录制中 → 一律回 Record 视图（恢复当前录制状态）；未录制 → 保留最近的
        /// 内部模式（Detail 只在详情仍有效时保留）。简单一致，不建导航历史。
        /// </summary>
        public void OnPageEntered()
        {
            if (IsRecording)
            {
                PageMode = SessionPageMode.Record;
            }
            else if (PageMode == SessionPageMode.Detail && !HasResult)
            {
                PageMode = SessionPageMode.Record;
            }
        }

        /// <summary>
        /// M4.5E.1 Gate B：唯一进入 Detail 的入口——History 查看与 Stop 完成统一走这里。
        /// Detail 展示的会话 = 本方法收到的 session 对象，绝不从 History 列表选择、
        /// 列表排序或上一次浏览状态推断。
        /// </summary>
        private void ShowDetail(TelemetryRecordingSession session)
        {
            BuildSummary(session);
            SetCurrentDetailSession(session.Id);

            // 先载入 Windows 事件证据行（旧 Session 无文件 → NotCaptured），
            // 再恢复 AI 状态——RenderAnalysis 的 AI 标记刷新需要行已存在才能落到对应 incident 上。
            LoadIncidentEvidence(session.Id);

            // 切换展示会话必须重置上一会话的 AI 展示状态——
            // 修复“浏览过 A 的分析残留到 B 的 Detail”（stale 的另一半）。
            ResetAnalysisState();
            var analysis = _analysisStore.Load(session.Id);
            if (analysis is not null)
            {
                RenderAnalysis(
                    analysis.Result,
                    analysis.ModelName,
                    analysis.DurationMs,
                    analysis.ContextJson,
                    analysis.SessionId);
            }

            PageMode = SessionPageMode.Detail;
        }

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
            // Gate C ①：Stop → completed session（recorder 内部 finalize + session.json 落盘）。
            // 这里拿到的就是刚刚完成的 B 本体，绝不允许再从列表/选择反推。
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

            SyncFromRecorder();

            // Gate C ②：Windows 证据采集阶段——针对刚刚完成的 B（Recorder 本身不碰事件日志）。
            await CaptureIncidentsAsync(session).ConfigureAwait(true);

            // Gate C ③：刷新历史集合。M4.5E.1：不再从列表反推“最新一条”，
            // 也绝不动 History 选择（SelectedRecent 保持用户原样）。
            LoadRecent();

            // Gate C ④/⑤/⑥：DisplayedSession = B → 载入 B 的 incident/analysis 状态 → Mode = Detail。
            ShowDetail(session);
        }

        /// <summary>
        /// Gate E 失败隔离：session.json 成功 + incidents 采集/落盘失败时，
        /// Session 仍然有效。仅留痕，不改录制结果；查询失败状态（Partial 等）
        /// 由 correlation service 照常写入 envelope，不算异常。本轮不加 UI 错误框。
        /// </summary>
        private async Task CaptureIncidentsAsync(TelemetryRecordingSession session)
        {
            try
            {
                await _incidentCorrelation.CaptureAsync(session).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(exception, "Session incidents capture");
            }
        }

        /// <summary>
        /// Gate C/L：只读载入已持久化的 incidents.json（绝不重新查询事件日志）。
        /// 旧 Session 无文件是合法状态（NotCaptured，不报错）；损坏文件 Load 返回 null
        /// → 同 NotCaptured，绝不影响会话本体展示。UI display cap 50 只影响展示。
        /// </summary>
        private void LoadIncidentEvidence(string sessionId)
        {
            IncidentRows.Clear();
            _incidentEvidenceLookup.Clear();
            HasIncidentRows = false;
            HasIncidentOverflow = false;
            HasIncidentQualityText = false;
            IncidentQualityText = string.Empty;
            IncidentOverflowText = string.Empty;
            IncidentHeaderText = "Windows 事件证据";

            var envelope = _incidentStore.Load(sessionId);
            if (envelope is null)
            {
                IncidentStateText = "未采集 Windows 事件证据";
                HasIncidentStateText = true;
                return;
            }

            if (envelope.QueryStatus != IncidentQueryStatus.Success)
            {
                // Gate C：查询失败状态是 data-quality information，不弹 MessageBox；
                // 空列表与非空列表都要能看到该提示。
                IncidentQualityText = envelope.QueryStatus switch
                {
                    IncidentQueryStatus.Partial => "部分事件日志不可用",
                    IncidentQueryStatus.PermissionDenied => "没有读取事件日志的权限",
                    IncidentQueryStatus.Unavailable => "事件日志在本机不可用",
                    _ => "事件日志查询失败",
                };
                HasIncidentQualityText = true;
            }

            if (envelope.Incidents.Count == 0)
            {
                // 与“未采集”必须区分：采集过，但窗口内没有已识别事件。
                IncidentStateText = "本次关联窗口内未发现已识别的 Windows 事件";
                HasIncidentStateText = true;
                return;
            }

            HasIncidentStateText = false;
            IncidentStateText = string.Empty;
            IncidentHeaderText = $"Windows 事件证据 · {envelope.Incidents.Count} 条";

            foreach (var incident in envelope.Incidents.Take(IncidentDisplayCap))
            {
                IncidentRows.Add(new SessionIncidentRow(incident, isInAiContext: false));
            }

            HasIncidentRows = IncidentRows.Count > 0;
            if (envelope.Incidents.Count > IncidentDisplayCap)
            {
                IncidentOverflowText = $"已显示前 {IncidentDisplayCap} 条，共 {envelope.Incidents.Count} 条";
                HasIncidentOverflow = true;
            }

            foreach (var incident in envelope.Incidents)
            {
                _incidentEvidenceLookup[incident.EvidenceId] =
                    incident.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + " · " + SessionIncidentRow.CategoryDisplayName(incident.Category)
                    + " · " + incident.ProviderName + " Event " + incident.EventId;
            }
        }

        /// <summary>Gate H：从 analysis ContextJson（组合上下文）解析真正进入 AI 的 incident EvidenceId。</summary>
        private void RefreshIncidentAiMarkers(string? contextJson)
        {
            HashSet<string> included;
            try
            {
                included = ParseAiIncidentIds(contextJson);
            }
            catch (Exception)
            {
                // ContextJson 异常时宁可没有标记，也不让展示崩掉。
                included = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var row in IncidentRows)
            {
                row.SetAiContext(included.Contains(row.EvidenceId));
            }
        }

        private static HashSet<string> ParseAiIncidentIds(string? contextJson)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(contextJson))
            {
                return result;
            }

            using var document = JsonDocument.Parse(contextJson);
            if (!document.RootElement.TryGetProperty("WindowsIncidents", out var incidentsElement)
                || incidentsElement.ValueKind != JsonValueKind.Object
                || !incidentsElement.TryGetProperty("Incidents", out var listElement)
                || listElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in listElement.EnumerateArray())
            {
                if (item.TryGetProperty("EvidenceId", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && idElement.GetString() is { } id)
                {
                    result.Add(id);
                }
            }

            return result;
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
                    session.Samples.Count,
                    // M4.5E.1 补充：已分析/未分析状态（只查文件存在，绝不触发分析）。
                    isAnalyzed: _analysisStore.AnalysisExists(session.Id)));
            }

            OnPropertyChanged(nameof(HasNoHistory));
        }

        private void SelectRecent()
        {
            // V2-M4.5E Gate I：录制中也允许浏览 Detail（只读历史会话；BuildSummary 只写
            // Statistics/Events，与录制中的 LiveMetrics 互不干扰）。绝不动 recorder。
            if (SelectedRecent is null)
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

            // M4.5E.1 Gate B：History → Detail 统一经唯一入口。
            ShowDetail(full);
        }

        /// <summary>切换会话时清空上一会话的 AI 展示状态（Gate 0.3）。</summary>
        private void ResetAnalysisState()
        {
            HasAnalysis = false;
            AssessmentBadge = string.Empty;
            ConfidenceText = string.Empty;
            AnalysisSummary = string.Empty;
            AnalysisDurationText = string.Empty;
            AnalysisError = string.Empty;
            SpokenSummary = string.Empty;
            Findings.Clear();
            Recommendations.Clear();
            Uncertainties.Clear();
            _voiceFailureText = null;
            VoiceState = VoicePlaybackState.Idle;
            // V2-M4.5D Gate Q：切换会话清空后同样要刷新播放按钮可用性。
            PlaySpokenSummaryCommand.NotifyCanExecuteChanged();
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

            // V2-M4.5E Gate J(12)：删掉的若是当前 Detail 载入的会话，Detail 状态一并失效，
            // 避免“详情悬挂”。删除只发生在 History 视图——保持 History，不清走用户位置。
            if (_currentSessionId == id)
            {
                HasResult = false;
                SetCurrentDetailSession(null);
                ResetAnalysisState();
            }
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
            // M4.5E.2 Gate B：开始时捕获目标会话，本次 operation（上下文/请求/持久化/渲染）
            // 全程绑定它；await 之后绝不重读 CurrentDetailSessionId 判断结果归属。
            var targetSessionId = CurrentDetailSessionId;
            if (IsAnalyzing || targetSessionId is null)
            {
                return;
            }

            var session = _store.Load(targetSessionId);
            if (session is null)
            {
                AnalysisError = "会话不存在，无法分析。";
                return;
            }

            IsAnalyzing = true;                          // 全局并发守卫（当前只允许一个分析）
            _activeAnalysisSessionId = targetSessionId;  // per-session 显示归属
            OnPropertyChanged(nameof(IsCurrentDetailAnalyzing));
            AnalysisError = string.Empty;
            var contextJson = string.Empty;
            try
            {
                var summary = session.Summary ?? TelemetrySessionAnalyzer.Analyze(session);
                var telemetryContext = TelemetrySessionAnalyzer.BuildAnalysisContext(
                    session with { Summary = summary });

                // V2-M4.3：组合有界确定性证据（Gate H）。旧 Session 无 incidents.json 属
                // 合法状态 → telemetry-only；不 backfill、不重新查询 Windows Event Log。
                // reducer 只影响 AI 输入，incidents.json 原样保留。
                var incidentEnvelope = _incidentStore.Load(session.Id);
                var evidenceContext = DiagnosticEvidenceContextBuilder.Build(
                    telemetryContext, incidentEnvelope);
                contextJson = JsonSerializer.Serialize(evidenceContext);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var run = await _analysisService.AnalyzeAsync(evidenceContext, CancellationToken.None);
                sw.Stop();

                if (!run.Success || run.Result is null)
                {
                    // Gate J：失败只写给“仍在目标会话上”的可见界面，不污染其它 Detail。
                    if (CurrentDetailSessionId == targetSessionId)
                    {
                        AnalysisError = $"AI 分析失败：{run.ErrorMessage}（请求 {run.RequestCount} 次）";
                    }
                    return;
                }

                contextJson = run.EvidenceContextJson ?? contextJson;

                // Gate D ①：持久化永远属于目标会话（analysis.json for A），与界面无关。
                var saved = PersistAnalysis(targetSessionId, run.Result, run.ModelName,
                    sw.ElapsedMilliseconds, contextJson, run.RepairUsed);

                // Gate D ②：刷新历史元数据（“已分析”标记），无论当前 Detail 是谁。
                LoadRecent();

                // Gate D ③：只有用户仍在目标会话上才渲染到可见 Detail；
                // 否则绝不碰当前 B 的 Summary/Findings/SpokenSummary——切回 A 时经 analysis.json 恢复。
                if (CurrentDetailSessionId == targetSessionId)
                {
                    if (!saved)
                    {
                        AnalysisError = "分析结果已生成，但保存 analysis.json 失败。";
                    }

                    RenderAnalysis(run.Result, run.ModelName, sw.ElapsedMilliseconds,
                        contextJson, targetSessionId);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消：保留旧分析。
            }
            catch (Exception exception)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(exception, "Sessions analyze");
                if (CurrentDetailSessionId == targetSessionId)
                {
                    AnalysisError = "AI 分析失败：" + exception.Message;
                }
            }
            finally
            {
                IsAnalyzing = false;
                _activeAnalysisSessionId = null;
                OnPropertyChanged(nameof(IsCurrentDetailAnalyzing));
            }
        }

        /// <summary>
        /// M4.5E.2 Gate D：把 AI 结果渲染到“当前可见”的 Detail——调用方必须保证
        /// CurrentDetailSessionId == sessionId。持久化由 PersistAnalysis 独立负责。
        /// </summary>
        private void RenderAnalysis(
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
            _voiceFailureText = null;
            VoiceState = VoicePlaybackState.Idle;
            // V2-M4.5D Gate Q 回归修复：HasAnalysis/SpokenSummary 变化不经过
            // VoiceState setter，RelayCommand 无 CommandManager 自动重查询——
            // 必须显式刷新，否则播放按钮在分析完成后仍是禁用态（点击无反应）。
            PlaySpokenSummaryCommand.NotifyCanExecuteChanged();

            // Gate H：ContextJson 记录了 AI 实际看到的组合上下文——
            // 据此标记哪些 incident 真正进入了 AI 分析（omitted 的不标）。
            RefreshIncidentAiMarkers(contextJson);

            // V2-M4.5D：分析完成 → 自动预生成语音并按会话缓存（命中缓存则跳过）。
            StartVoiceGenerationIfMissing();
        }

        /// <summary>
        /// Gate D ①：持久化属于目标会话（analysis.json），与“当前 Detail 是谁”无关。
        /// 保存失败只返回 false，由调用方决定是否展示（仅在用户仍看着目标会话时）。
        /// </summary>
        private bool PersistAnalysis(
            string sessionId,
            SessionAnalysisResult result,
            string modelName,
            long durationMs,
            string contextJson,
            bool repairUsed)
        {
            try
            {
                _analysisStore.Save(new SessionAnalysisEnvelope(
                    SchemaVersion: 1,
                    SessionId: sessionId,
                    AnalyzedAtUtc: DateTimeOffset.UtcNow,
                    ModelName: modelName,
                    DurationMs: durationMs,
                    RepairUsed: repairUsed,
                    Result: result,
                    ContextJson: contextJson));
                return true;
            }
            catch (Exception exception)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(exception, "SessionAnalysis save");
                return false;
            }
        }

        /// <summary>
        /// 证据 ID 映射为可读文本（§30 + Gate I）：
        /// incident:XXXX → "HH:mm:ss · 类别 · Provider Event N"；找不到时明确回退，
        /// 绝不把未知 ID 当成事件内容展示，也绝不直接向用户显示原始 ID。
        /// </summary>
        public string DescribeEvidence(string evidenceId)
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

            if (evidenceId.StartsWith("incident:", StringComparison.Ordinal))
            {
                return _incidentEvidenceLookup.TryGetValue(evidenceId, out var text)
                    ? text
                    : "Windows 事件证据不可用";
            }

            return evidenceId;
        }

        /// <summary>
        /// M4.5E.2 Gate J：播放绑定发起时的目标会话。缓存命中直接播；
        /// 用户中途切走时完成回调不污染当前 Detail、也不在别的会话页上突然出声。
        /// </summary>
        public void PlaySpokenSummary()
        {
            var sessionId = CurrentDetailSessionId;
            if (VoiceState is VoicePlaybackState.Generating or VoicePlaybackState.Playing
                || string.IsNullOrWhiteSpace(SpokenSummary)
                || sessionId is null)
            {
                return;
            }

            var text = SpokenSummary;
            var configuration = _voiceSnapshot();
            _activeVoiceSessionId = sessionId;
            _voiceFailureText = null;
            VoiceState = VoicePlaybackState.Generating;
            _ = Task.Run(async () =>
            {
                try
                {
                    // V2-M4.5D：会话级 voice.wav 缓存命中 → 不再重复调用 GPT-SoVITS
                    //（M4.5E.2 Gate K-14：损坏/空文件按未命中处理，自动重新合成）。
                    byte[]? wav = _analysisStore.TryLoadVoiceWav(sessionId);
                    if (wav is null)
                    {
                        var result = await _voiceService.SynthesizeAsync(text, configuration, CancellationToken.None);
                        if (!result.Succeeded || result.WavBytes is null)
                        {
                            if (CurrentDetailSessionId == sessionId)
                            {
                                ReportVoiceFailureOnTarget(sessionId, result.ErrorMessage ?? "语音合成失败。");
                            }

                            FinishVoiceOperation(sessionId, failed: true, failureText: "语音播放失败");
                            return;
                        }

                        wav = result.WavBytes;
                        _analysisStore.SaveVoiceWav(sessionId, wav);
                    }

                    // Gate J：只有用户仍停留在发起会话上才真正出声。
                    // M4.5E.3 Gate E：VoiceState 必须经捕获的 UI 上下文变更——后台线程直接
                    // set_VoiceState 会同步触发 CanExecuteChanged（VerifyAccess 异常），
                    // 且发生在 PlayWav 之前 → 无声无报错（E.2 播放回归根因）。
                    if (CurrentDetailSessionId == sessionId)
                    {
                        RunOnUiThread(() => VoiceState = VoicePlaybackState.Playing);
                        _wavPlayback.PlayWav(wav);
                    }

                    FinishVoiceOperation(sessionId, failed: false);
                }
                catch (Exception exception)
                {
                    Services.Diagnostics.ExceptionLogWriter.Write(exception, "Sessions voice playback");
                    if (CurrentDetailSessionId == sessionId)
                    {
                        ReportVoiceFailureOnTarget(sessionId, exception.Message);
                    }

                    FinishVoiceOperation(sessionId, failed: true, failureText: "语音播放失败");
                }
            });
        }

        /// <summary>
        /// M4.5E.2 Gate J：语音预生成绑定发起时的目标会话（只对可见会话发起）；
        /// 完成回调只影响目标会话的显示——用户已切走时绝不污染当前 Detail。
        /// </summary>
        private void StartVoiceGenerationIfMissing()
        {
            var sessionId = CurrentDetailSessionId;
            if (string.IsNullOrWhiteSpace(SpokenSummary)
                || VoiceState is VoicePlaybackState.Generating or VoicePlaybackState.Playing
                || sessionId is null
                || _analysisStore.HasCachedVoice(sessionId))
            {
                return;
            }

            var text = SpokenSummary;
            var configuration = _voiceSnapshot();
            if (string.IsNullOrWhiteSpace(configuration.Endpoint))
            {
                return;
            }

            _activeVoiceSessionId = sessionId;
            VoiceState = VoicePlaybackState.Generating;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _voiceService.SynthesizeAsync(text, configuration, CancellationToken.None);
                    if (!result.Succeeded || result.WavBytes is null)
                    {
                        FinishVoiceOperation(
                            sessionId, failed: true, failureText: "语音预生成失败，播放时会重试");
                        return;
                    }

                    // Gate G：缓存先落盘（磁盘是事实源），再结束操作状态——
                    // 完成后 VoiceStateText 按缓存事实自动显示“已生成”。
                    _analysisStore.SaveVoiceWav(sessionId, result.WavBytes);
                    FinishVoiceOperation(sessionId, failed: false);
                }
                catch (Exception exception)
                {
                    Services.Diagnostics.ExceptionLogWriter.Write(exception, "Sessions voice pregenerate");
                    FinishVoiceOperation(
                        sessionId, failed: true, failureText: "语音预生成失败，播放时会重试");
                }
            });
        }

        /// <summary>
        /// M4.5E.3 Gate E：后台语音线程的唯一 UI 状态回写通道。使用构造时捕获的
        /// SynchronizationContext；无捕获上下文或已在同一上下文 → 内联执行
        /// （单元测试 / 无 WPF 宿主）。绝不依赖 Application.Current。
        /// </summary>
        private void RunOnUiThread(Action action)
        {
            var context = _uiSynchronizationContext;
            if (context is null || Environment.CurrentManagedThreadId == _uiThreadId)
            {
                action();
                return;
            }

            context.Post(static state =>
            {
                try
                {
                    ((Action)state!).Invoke();
                }
                catch (Exception exception)
                {
                    // Post 回调逃逸异常会变成 UI 线程未处理异常——留痕即可。
                    Services.Diagnostics.ExceptionLogWriter.Write(exception, "Sessions voice UI state");
                }
            }, action);
        }

        /// <summary>
        /// M4.5E.3 Gate F：失败只对仍在目标会话上的用户可见——ErrorText 承载技术细节，
        /// 简短用户文案由 VoiceStateText（_voiceFailureText）呈现。
        /// </summary>
        private void ReportVoiceFailureOnTarget(string sessionId, string technicalMessage)
        {
            RunOnUiThread(() =>
            {
                if (CurrentDetailSessionId == sessionId)
                {
                    ErrorText = technicalMessage;
                    HasError = true;
                }
            });
        }

        /// <summary>
        /// 结束一次语音操作：清除 per-session 忙标记；失败只对仍在目标会话上的用户可见。
        /// M4.5E.3 Gate E：本方法从后台语音线程调用，所有状态变更经 RunOnUiThread 回到
        /// UI 上下文——后台线程直接 set_VoiceState 会同步触发 CanExecuteChanged 的
        /// VerifyAccess 异常（真机日志实锤的播放无声回归根因，禁止改回）。
        /// </summary>
        private void FinishVoiceOperation(string sessionId, bool failed, string? failureText = null)
        {
            RunOnUiThread(() =>
            {
                _activeVoiceSessionId = null;
                if (CurrentDetailSessionId == sessionId)
                {
                    if (failed)
                    {
                        _voiceFailureText = failureText ?? "语音预生成失败，播放时会重试";
                        VoiceState = VoicePlaybackState.Error;
                    }
                    else
                    {
                        _voiceFailureText = null;
                        VoiceState = VoicePlaybackState.Idle;
                    }
                }
                else
                {
                    // 用户在其它会话上：全局操作状态归位，但当前 Detail 的显示不被触碰。
                    _voiceFailureText = null;
                    VoiceState = VoicePlaybackState.Idle;
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



