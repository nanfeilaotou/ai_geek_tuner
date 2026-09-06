using System.Collections.ObjectModel;
using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Transport;
using AIGeekTuner.Services.Dialogs;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.ViewModels
{
    /// <summary>创建入口的预设类型（只是预设，不是新的 Provider Kind）。</summary>
    public enum AiProviderCreationPreset
    {
        Ollama,
        LmStudio,
        CustomOpenAiCompatible
    }

    /// <summary>
    /// Provider 选择器里的一项：只承载已保存的 profile（创建入口是独立的“＋ 添加 Provider”菜单）。
    /// </summary>
    public sealed class AiProviderSelectorItem
    {
        public string Label { get; init; } = string.Empty;

        public string? ExistingProfileId { get; init; }
    }

    /// <summary>结构化输出模式下拉项。</summary>
    public sealed record AiStructuredOutputModeItem(AiStructuredOutputMode Mode, string Label);

    /// <summary>
    /// “AI 服务提供方”设置卡片视图模型（V2-M5.1A）。
    /// 整个编辑界面是草稿：刷新模型 / 测试连接 / 测试结构化输出都使用当前 UI 草稿，
    /// 绝不要求先保存；只有“保存 Provider”走 manager 的原子保存与凭据回滚语义。
    /// API Key 明文只经过瞬时字段（不进任何展示属性 / 日志 / 状态文案），
    /// 永远不回填到界面——已存密钥只显示“已配置”状态。
    /// 本卡片只管理 Provider 配置本身；M5.1B 之前诊断与 Session AI 仍走旧 Ollama runtime。
    /// </summary>
    public sealed class AiProviderSettingsViewModel : ViewModelBase
    {
        private const string ModelsUnavailableMessage = "该服务未提供可用的模型列表，可手动输入模型 ID。";
        private const string AuthenticationFailedMessage = "认证失败，请检查 API Key。";
        private const string ConnectionUnavailableMessage = "无法连接到服务，请确认服务已启动、地址正确。";

        private static readonly AiStructuredOutputModeItem[] StructuredOutputModeItems =
        [
            new(AiStructuredOutputMode.NativeSchema, "Native Schema（Ollama 原生）"),
            new(AiStructuredOutputMode.OpenAiJsonSchema, "OpenAI JSON Schema"),
            new(AiStructuredOutputMode.JsonObject, "JSON Object"),
            new(AiStructuredOutputMode.PromptOnly, "Prompt Only（仅提示词）")
        ];

        private readonly IAiProviderManager? _manager;
        private readonly IConfirmationDialogService? _confirmationDialog;
        private readonly Func<string, Task<bool>>? _credentialExists;

        /// <summary>已保存基线（选中既有 profile 时的快照；新建草稿为 null）。</summary>
        private AiProviderProfile? _baselineProfile;

        /// <summary>脏比较基准：加载 / 新建时的草稿快照。</summary>
        private AiProviderProfile? _referenceDraft;

        private bool _suppressDirty;
        private bool _suppressSelection;

        private AiProviderSelectorItem? _selectedItem;
        private string _displayName = string.Empty;
        private string _baseUrl = string.Empty;
        private string _modelText = string.Empty;
        private string _defaultModelText = string.Empty;
        private AiStructuredOutputMode _structuredOutputMode = AiStructuredOutputMode.PromptOnly;
        private bool _isNewDraft;

        // API Key 明文只存在于该瞬时字段；绝不进入任何展示属性 / 状态 / 日志。
        private string? _apiKeyInput;
        private bool _isKeyEditorVisible;
        private bool _isKeyPendingClear;
        private bool _baselineHasCredential;

        private bool _isLoadingModels;
        private bool _isTestingConnection;
        private bool _isTestingStructuredOutput;
        private bool _isSaving;

        private bool _hasUnsavedChanges;

        // ---- V2-M5.1B（Gate M）：Active Provider（“当前使用”）状态 ----
        private string _activeProviderDisplay = "尚未配置";
        private bool _isEditingProfileActive;
        private bool _canActivateCurrentProfile;

        private string _statusMessage = "选择或添加一个 Provider 开始配置。";
        private SettingsStatusKind _statusKind = SettingsStatusKind.Info;
        private string _structuredProbeText = string.Empty;

        private readonly RelayCommand _createOllamaProviderCommand;
        private readonly RelayCommand _createLmStudioProviderCommand;
        private readonly RelayCommand _createCustomProviderCommand;
        private readonly AsyncRelayCommand _refreshModelsCommand;
        private readonly AsyncRelayCommand _testConnectionCommand;
        private readonly AsyncRelayCommand _testStructuredOutputCommand;
        private readonly AsyncRelayCommand _saveProviderCommand;
        private readonly AsyncRelayCommand _deleteProviderCommand;
        private readonly RelayCommand _beginKeyUpdateCommand;
        private readonly RelayCommand _clearKeyCommand;
        private readonly AsyncRelayCommand _activateCurrentProfileCommand;

        public AiProviderSettingsViewModel(
            IAiProviderManager? manager,
            IConfirmationDialogService? confirmationDialog = null,
            Func<string, Task<bool>>? credentialExists = null)
        {
            _manager = manager;
            _confirmationDialog = confirmationDialog;
            _credentialExists = credentialExists;

            _refreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, () => !IsLoadingModels);
            _testConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsTestingConnection);
            _testStructuredOutputCommand = new AsyncRelayCommand(
                TestStructuredOutputAsync, () => !IsTestingStructuredOutput);
            _saveProviderCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
            _deleteProviderCommand = new AsyncRelayCommand(DeleteAsync, () => !IsSaving);
            _beginKeyUpdateCommand = new RelayCommand(BeginKeyUpdate);
            _clearKeyCommand = new RelayCommand(MarkKeyPendingClear);
            _activateCurrentProfileCommand = new AsyncRelayCommand(ActivateCurrentProfileAsync);
            _createOllamaProviderCommand = new RelayCommand(
                () => StartCreateProvider(AiProviderCreationPreset.Ollama));
            _createLmStudioProviderCommand = new RelayCommand(
                () => StartCreateProvider(AiProviderCreationPreset.LmStudio));
            _createCustomProviderCommand = new RelayCommand(
                () => StartCreateProvider(AiProviderCreationPreset.CustomOpenAiCompatible));

            ReloadProfiles(selectId: null);
        }

        /// <summary>无 manager 的降级实例（旧测试 / 特殊环境）：卡片可见但操作不可用。</summary>
        public static AiProviderSettingsViewModel CreateUnavailable()
        {
            return new AiProviderSettingsViewModel(null)
            {
                _statusMessage = "Provider 配置组件在此环境未启用。",
                _statusKind = SettingsStatusKind.Warning
            };
        }

        /// <summary>组合根是否注入了 Provider 管理器。</summary>
        public bool IsAvailable => _manager is not null;

        public ObservableCollection<AiProviderSelectorItem> SelectorItems { get; } = [];

        public ObservableCollection<string> ModelOptions { get; } = [];

        public IReadOnlyList<AiStructuredOutputModeItem> StructuredOutputModes => StructuredOutputModeItems;

        public AiProviderSelectorItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (_suppressSelection)
                {
                    _selectedItem = value;
                    OnPropertyChanged();
                    return;
                }

                if (ReferenceEquals(_selectedItem, value))
                {
                    return;
                }

                // Gate M：草稿未保存时不偷偷丢弃——确认放弃才切换，否则回退选择。
                if (HasUnsavedChanges && !ConfirmDiscardChanges())
                {
                    _suppressSelection = true;
                    OnPropertyChanged();
                    _suppressSelection = false;
                    return;
                }

                _selectedItem = value;
                OnPropertyChanged();
                OnSelectionChanged(value);
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetProperty(ref _displayName, value))
                {
                    UpdateDirty();
                }
            }
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                if (SetProperty(ref _baseUrl, value))
                {
                    UpdateDirty();
                }
            }
        }

        public string ModelText
        {
            get => _modelText;
            set
            {
                if (SetProperty(ref _modelText, value))
                {
                    UpdateDirty();
                }
            }
        }

        public string DefaultModelText
        {
            get => _defaultModelText;
            set
            {
                if (SetProperty(ref _defaultModelText, value))
                {
                    UpdateDirty();
                }
            }
        }

        public AiStructuredOutputMode SelectedStructuredOutputMode
        {
            get => _structuredOutputMode;
            set
            {
                if (SetProperty(ref _structuredOutputMode, value))
                {
                    UpdateDirty();
                }
            }
        }

        public string KindDisplay => SelectedItem?.ExistingProfileId is not null || IsNewDraft
            ? BuildDraft().Kind switch
            {
                AiProviderKind.OllamaNative => "Ollama Native",
                AiProviderKind.OpenAiCompatible => "OpenAI Compatible",
                _ => "未知"
            }
            : string.Empty;

        /// <summary>稳定 ID（创建后不可修改）；普通用户只需在高级区 / tooltip 看到。</summary>
        public string ProviderIdDisplay { get; private set; } = string.Empty;

        public bool IsNewDraft
        {
            get => _isNewDraft;
            private set => SetProperty(ref _isNewDraft, value);
        }

        public bool HasSelection => SelectedItem is not null;

        // ---- V2-M5.1B（Gate M）：“当前使用”的 Provider 状态 ----

        /// <summary>“当前使用 AI”展示行，例如 “LM Studio · qwen2.5-coder:14b”；无可用 Provider 时为“尚未配置”。</summary>
        public string ActiveProviderDisplay
        {
            get => _activeProviderDisplay;
            private set => SetProperty(ref _activeProviderDisplay, value);
        }

        /// <summary>当前编辑的已保存 profile 就是 Active Provider（XAML 显示“● 当前正在使用”）。</summary>
        public bool IsEditingProfileActive
        {
            get => _isEditingProfileActive;
            private set => SetProperty(ref _isEditingProfileActive, value);
        }

        /// <summary>当前编辑的是已保存 profile 且不是 Active（XAML 显示“设为当前使用”按钮）。</summary>
        public bool CanActivateCurrentProfile
        {
            get => _canActivateCurrentProfile;
            private set => SetProperty(ref _canActivateCurrentProfile, value);
        }

        public System.Windows.Input.ICommand ActivateCurrentProfileCommand => _activateCurrentProfileCommand;

        public bool HasStoredCredential => _baselineHasCredential;

        public string CredentialStatusText
        {
            get
            {
                if (_isKeyPendingClear)
                {
                    return _baselineHasCredential
                        ? "已标记清除（保存 Provider 后生效）"
                        : "未配置";
                }

                if (!string.IsNullOrEmpty(_apiKeyInput))
                {
                    return "已输入新密钥，保存 Provider 后替换";
                }

                return _baselineHasCredential ? "已配置（不显示明文）" : "未配置";
            }
        }

        public bool IsKeyEditorVisible
        {
            get => _isKeyEditorVisible;
            private set => SetProperty(ref _isKeyEditorVisible, value);
        }

        public bool IsKeyPendingClear
        {
            get => _isKeyPendingClear;
            private set
            {
                if (SetProperty(ref _isKeyPendingClear, value))
                {
                    OnPropertyChanged(nameof(CredentialStatusText));
                    UpdateDirty();
                }
            }
        }

        public bool IsLoadingModels
        {
            get => _isLoadingModels;
            private set
            {
                if (SetProperty(ref _isLoadingModels, value))
                {
                    _refreshModelsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            private set
            {
                if (SetProperty(ref _isTestingConnection, value))
                {
                    _testConnectionCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsTestingStructuredOutput
        {
            get => _isTestingStructuredOutput;
            private set
            {
                if (SetProperty(ref _isTestingStructuredOutput, value))
                {
                    _testStructuredOutputCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (SetProperty(ref _isSaving, value))
                {
                    _saveProviderCommand.NotifyCanExecuteChanged();
                    _deleteProviderCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public SettingsStatusKind StatusKind
        {
            get => _statusKind;
            private set => SetProperty(ref _statusKind, value);
        }

        public string StructuredProbeText
        {
            get => _structuredProbeText;
            private set => SetProperty(ref _structuredProbeText, value);
        }

        public AsyncRelayCommand RefreshModelsCommand => _refreshModelsCommand;

        public AsyncRelayCommand TestConnectionCommand => _testConnectionCommand;

        public AsyncRelayCommand TestStructuredOutputCommand => _testStructuredOutputCommand;

        public AsyncRelayCommand SaveProviderCommand => _saveProviderCommand;

        public AsyncRelayCommand DeleteProviderCommand => _deleteProviderCommand;

        public RelayCommand BeginKeyUpdateCommand => _beginKeyUpdateCommand;

        public RelayCommand ClearKeyCommand => _clearKeyCommand;

        public RelayCommand CreateOllamaProviderCommand => _createOllamaProviderCommand;

        public RelayCommand CreateLmStudioProviderCommand => _createLmStudioProviderCommand;

        public RelayCommand CreateCustomProviderCommand => _createCustomProviderCommand;

        /// <summary>密钥输入框需要被清空时通知视图（PasswordBox 无法从 VM 直接清空）。</summary>
        public event Action? ApiKeyInputReset;

        /// <summary>显式导入 Provider 配置后重读持久化快照，保留当前选择（若仍存在）。</summary>
        public void ReloadFromPersistence()
        {
            var selectedId = SelectedItem?.ExistingProfileId;
            ReloadProfiles(selectedId);
        }

        // ============================================================
        // 选择 / 草稿加载
        // ============================================================

        private void OnSelectionChanged(AiProviderSelectorItem? item)
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(KindDisplay));
            RefreshActiveProviderState();

            if (item is null)
            {
                ClearDraft();
                return;
            }

            var profile = _manager?.GetProfile(item.ExistingProfileId!);
            if (profile is null)
            {
                ClearDraft();
                return;
            }

            LoadFromProfile(profile);
            _ = RefreshCredentialStatusAsync();
        }

        private void ClearDraft()
        {
            _baselineProfile = null;
            _referenceDraft = null;
            _suppressDirty = true;
            DisplayName = string.Empty;
            BaseUrl = string.Empty;
            ModelText = string.Empty;
            DefaultModelText = string.Empty;
            SelectedStructuredOutputMode = AiStructuredOutputMode.PromptOnly;
            ModelOptions.Clear();
            _suppressDirty = false;
            ProviderIdDisplay = string.Empty;
            OnPropertyChanged(nameof(ProviderIdDisplay));
            IsNewDraft = false;
            ResetKeyInput();
            _baselineHasCredential = false;
            OnPropertyChanged(nameof(CredentialStatusText));
            OnPropertyChanged(nameof(HasStoredCredential));
            HasUnsavedChanges = false;
            SetStatus(SettingsStatusKind.Info, "请选择或添加一个 Provider。");
        }

        /// <summary>
        /// “＋ 添加 Provider”菜单入口：新建预设草稿。
        /// 复用原有 preset / ID 生成 / dirty 保护语义，不新建状态机；
        /// 用户放弃未保存修改时保持当前草稿不动。
        /// </summary>
        public void StartCreateProvider(AiProviderCreationPreset preset)
        {
            if (_manager is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 配置组件在此环境未启用。");
                return;
            }

            if (HasUnsavedChanges && !ConfirmDiscardChanges())
            {
                return;
            }

            LoadCreationDraft(preset);
        }

        private void LoadCreationDraft(AiProviderCreationPreset preset)
        {
            var (id, displayName, baseUrl, kind, mode) = preset switch
            {
                AiProviderCreationPreset.Ollama => (
                    GenerateUniqueId(AiProviderPresets.OllamaPresetId),
                    "Ollama",
                    AiProviderPresets.OllamaDefaultBaseUrl,
                    AiProviderKind.OllamaNative,
                    AiStructuredOutputMode.NativeSchema),
                AiProviderCreationPreset.LmStudio => (
                    GenerateUniqueId(AiProviderPresets.LmStudioPresetId),
                    "LM Studio",
                    AiProviderPresets.LmStudioDefaultBaseUrl,
                    AiProviderKind.OpenAiCompatible,
                    AiStructuredOutputMode.OpenAiJsonSchema),
                _ => (
                    GenerateNumberedUniqueId("custom"),
                    "自定义 OpenAI Compatible",
                    string.Empty,
                    AiProviderKind.OpenAiCompatible,
                    AiStructuredOutputMode.PromptOnly)
            };

            var draft = new AiProviderProfile
            {
                Id = id,
                DisplayName = displayName,
                Kind = kind,
                BaseUrl = baseUrl,
                Models = Array.Empty<AiProviderModel>(),
                StructuredOutputMode = mode,
                Enabled = true
            };

            _baselineProfile = null;
            _referenceDraft = draft;
            ApplyDraftToFields(draft);
            IsNewDraft = true;
            _baselineHasCredential = false;
            OnPropertyChanged(nameof(CredentialStatusText));
            OnPropertyChanged(nameof(HasStoredCredential));
            ResetKeyInput();
            HasUnsavedChanges = false;
            StructuredProbeText = string.Empty;
            SetStatus(SettingsStatusKind.Info, $"已创建“{displayName}”草稿，填写后点击“保存 Provider”。");
        }

        private void LoadFromProfile(AiProviderProfile profile)
        {
            _baselineProfile = profile;
            _referenceDraft = profile;
            ApplyDraftToFields(profile);
            IsNewDraft = false;
            ResetKeyInput();
            HasUnsavedChanges = false;
            StructuredProbeText = string.Empty;
            SetStatus(SettingsStatusKind.Info, "编辑不会立即生效，修改后点击“保存 Provider”。");
        }

        private void ApplyDraftToFields(AiProviderProfile profile)
        {
            _suppressDirty = true;
            ProviderIdDisplay = profile.Id;
            DisplayName = profile.DisplayName;
            BaseUrl = profile.BaseUrl;
            SelectedStructuredOutputMode = profile.StructuredOutputMode;
            ModelOptions.Clear();
            foreach (var model in profile.Models)
            {
                if (!string.IsNullOrWhiteSpace(model.Id))
                {
                    ModelOptions.Add(model.Id);
                }
            }

            ModelText = profile.DefaultModelId
                ?? profile.Models.FirstOrDefault()?.Id
                ?? string.Empty;
            DefaultModelText = profile.DefaultModelId ?? string.Empty;
            _suppressDirty = false;
            OnPropertyChanged(nameof(ProviderIdDisplay));
            OnPropertyChanged(nameof(KindDisplay));
        }

        private void ResetKeyInput()
        {
            _apiKeyInput = null;
            _isKeyPendingClear = false;
            IsKeyEditorVisible = false;
            OnPropertyChanged(nameof(CredentialStatusText));
            ApiKeyInputReset?.Invoke();
        }

        /// <summary>从 manager 重读已保存 profiles 并重建选择器；selectId 优先选中。</summary>
        private void ReloadProfiles(string? selectId)
        {
            var profiles = _manager?.ListProfiles() ?? [];

            _suppressSelection = true;
            SelectorItems.Clear();
            foreach (var profile in profiles)
            {
                SelectorItems.Add(new AiProviderSelectorItem
                {
                    Label = profile.DisplayName,
                    ExistingProfileId = profile.Id
                });
            }

            var target = selectId is null
                ? null
                : SelectorItems.FirstOrDefault(item => item.ExistingProfileId == selectId);
            _selectedItem = target ?? (profiles.Count > 0 ? SelectorItems[0] : null);
            _suppressSelection = false;
            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(HasSelection));
            OnSelectionChanged(_selectedItem);
            RefreshActiveProviderState();
        }

        /// <summary>从 manager 重读 Active Provider 解析结果并刷新相关展示属性。</summary>
        private void RefreshActiveProviderState()
        {
            var resolved = _manager?.ResolveActiveProvider();
            ActiveProviderDisplay = resolved is null
                ? "尚未配置"
                : resolved.DisplayName + " · " + resolved.DefaultModelId;

            var editingId = SelectedItem?.ExistingProfileId;
            IsEditingProfileActive = editingId is not null
                && string.Equals(editingId, resolved?.Id, StringComparison.Ordinal);
            CanActivateCurrentProfile = editingId is not null && !IsEditingProfileActive;
        }

        /// <summary>
        /// 把当前编辑的“已保存 profile”设为当前使用（Gate M Activation）。
        /// 草稿未保存（新建或脏）时绝不生效；激活的必须是已保存成功的 profile。
        /// </summary>
        private async Task ActivateCurrentProfileAsync()
        {
            if (_manager is null || SelectedItem?.ExistingProfileId is null)
            {
                SetStatus(SettingsStatusKind.Warning, "请先选择一个已保存的 Provider。");
                return;
            }

            if (HasUnsavedChanges)
            {
                SetStatus(SettingsStatusKind.Warning, "请先保存 Provider 配置，再设为当前使用。");
                return;
            }

            var providerId = SelectedItem.ExistingProfileId;
            try
            {
                var result = await _manager.SetActiveProviderAsync(providerId);
                if (!result.Success)
                {
                    SetStatus(SettingsStatusKind.Error, result.Error ?? "设置当前使用失败，请稍后重试。");
                    return;
                }

                RefreshActiveProviderState();
                var displayName = _manager.GetProfile(providerId)?.DisplayName ?? providerId;
                SetStatus(SettingsStatusKind.Success, "已将 " + displayName + " 设为当前使用的 AI 服务提供方。");
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "AiProviderSettings.Activate");
                SetStatus(SettingsStatusKind.Error, "设置当前使用失败，请稍后重试。");
            }
        }

        private string GenerateUniqueId(string baseId)
        {
            var existing = new HashSet<string>(
                (_manager?.ListProfiles() ?? []).Select(profile => profile.Id),
                StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(baseId))
            {
                return baseId;
            }

            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{baseId}-{suffix}";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>编号式 ID 生成（custom-1、custom-2……）；无冲突也从 1 开始。</summary>
        private string GenerateNumberedUniqueId(string prefix)
        {
            var existing = new HashSet<string>(
                (_manager?.ListProfiles() ?? []).Select(profile => profile.Id),
                StringComparer.OrdinalIgnoreCase);
            for (var index = 1; ; index++)
            {
                var candidate = $"{prefix}-{index}";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // ============================================================
        // 草稿构建 / 脏状态
        // ============================================================

        /// <summary>从当前 UI 状态构建草稿（含手动输入的模型自动并入 Models）。</summary>
        private AiProviderProfile BuildDraft()
        {
            var models = new List<AiProviderModel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ModelOptions)
            {
                var normalized = AiProviderModelId.Normalize(id);
                if (normalized is not null && seen.Add(normalized))
                {
                    models.Add(new AiProviderModel(normalized));
                }
            }

            foreach (var typed in new[] { ModelText, DefaultModelText })
            {
                var normalized = AiProviderModelId.Normalize(typed);
                if (normalized is not null && seen.Add(normalized))
                {
                    models.Add(new AiProviderModel(normalized));
                }
            }

            var defaultId = AiProviderModelId.Normalize(DefaultModelText)
                ?? AiProviderModelId.Normalize(ModelText);
            if (defaultId is null && models.Count == 1)
            {
                defaultId = models[0].Id;
            }

            return new AiProviderProfile
            {
                Id = ProviderIdDisplay,
                DisplayName = DisplayName.Trim(),
                Kind = _referenceDraft?.Kind ?? AiProviderKind.OpenAiCompatible,
                BaseUrl = BaseUrl.Trim(),
                Models = models,
                DefaultModelId = defaultId,
                StructuredOutputMode = SelectedStructuredOutputMode,
                Enabled = true
            };
        }

        /// <summary>测试 / 发现使用的明文 Key：仅取用户本次输入；否则由 manager 回退已存凭据。</summary>
        private string? GetPlainApiKeyOrNull() =>
            string.IsNullOrWhiteSpace(_apiKeyInput) ? null : _apiKeyInput;

        private AiCredentialChange ResolveCredentialChange()
        {
            if (_isKeyPendingClear)
            {
                return new AiCredentialChange(AiCredentialChangeMode.Delete);
            }

            if (!string.IsNullOrEmpty(_apiKeyInput))
            {
                return new AiCredentialChange(AiCredentialChangeMode.Replace, _apiKeyInput);
            }

            return AiCredentialChange.Keep;
        }

        private bool DraftEquals(AiProviderProfile left, AiProviderProfile right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.DisplayName.Trim(), right.DisplayName.Trim(), StringComparison.Ordinal)
                && string.Equals(left.BaseUrl.Trim(), right.BaseUrl.Trim(), StringComparison.Ordinal)
                && left.Kind == right.Kind
                && left.StructuredOutputMode == right.StructuredOutputMode
                && ModelsEqual(left.Models, right.Models)
                && string.Equals(
                    left.DefaultModelId?.Trim() ?? string.Empty,
                    right.DefaultModelId?.Trim() ?? string.Empty,
                    StringComparison.Ordinal);
        }

        private static bool ModelsEqual(IReadOnlyList<AiProviderModel> left, IReadOnlyList<AiProviderModel> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index].Id, right[index].Id, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void UpdateDirty()
        {
            if (_suppressDirty)
            {
                return;
            }

            var dirty = _referenceDraft is null
                ? false
                : !DraftEquals(BuildDraft(), _referenceDraft);
            if (!dirty && !string.IsNullOrEmpty(_apiKeyInput))
            {
                dirty = true;
            }

            if (!dirty && _isKeyPendingClear && _baselineHasCredential)
            {
                dirty = true;
            }

            HasUnsavedChanges = dirty;
        }

        private bool ConfirmDiscardChanges()
        {
            if (_confirmationDialog is null)
            {
                return true;
            }

            return _confirmationDialog.Confirm(
                "放弃未保存修改",
                "当前 Provider 有未保存修改，是否放弃？");
        }

        // ============================================================
        // 凭据状态 / API Key 交互
        // ============================================================

        /// <summary>刷新“已配置 / 未配置”状态（只取布尔结果，明文不进 ViewModel）。</summary>
        public async Task RefreshCredentialStatusAsync()
        {
            var id = ProviderIdDisplay;
            if (_credentialExists is null || string.IsNullOrWhiteSpace(id))
            {
                _baselineHasCredential = false;
            }
            else
            {
                try
                {
                    _baselineHasCredential = await _credentialExists(id);
                }
                catch
                {
                    _baselineHasCredential = false;
                }
            }

            OnPropertyChanged(nameof(HasStoredCredential));
            OnPropertyChanged(nameof(CredentialStatusText));
        }

        private void BeginKeyUpdate()
        {
            IsKeyEditorVisible = true;
            _apiKeyInput = null;
            _isKeyPendingClear = false;
            OnPropertyChanged(nameof(CredentialStatusText));
            ApiKeyInputReset?.Invoke();
        }

        private void MarkKeyPendingClear()
        {
            IsKeyEditorVisible = false;
            _apiKeyInput = null;
            ApiKeyInputReset?.Invoke();
            IsKeyPendingClear = true;
        }

        /// <summary>由视图的 PasswordBox 调用；明文只存进瞬时字段，绝不回显。</summary>
        public void SetApiKeyInput(string? password)
        {
            _apiKeyInput = string.IsNullOrEmpty(password) ? null : password;
            if (_apiKeyInput is not null && _isKeyPendingClear)
            {
                _isKeyPendingClear = false;
                OnPropertyChanged(nameof(IsKeyPendingClear));
                OnPropertyChanged(nameof(CredentialStatusText));
            }

            OnPropertyChanged(nameof(CredentialStatusText));
            UpdateDirty();
        }

        // ============================================================
        // 模型发现 / 连接测试 / 结构化输出探测
        // ============================================================

        private async Task RefreshModelsAsync()
        {
            if (_manager is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 配置组件在此环境未启用。");
                return;
            }

            IsLoadingModels = true;
            try
            {
                var draft = BuildDraft();
                var result = await _manager.FetchModelsAsync(draft, GetPlainApiKeyOrNull());
                switch (result.Status)
                {
                    case AiModelDiscoveryStatus.ModelsDiscovered:
                        MergeDiscoveredModels(result.Models);
                        SetStatus(
                            SettingsStatusKind.Success,
                            $"已发现 {result.Models.Count} 个模型（合并后共 {ModelOptions.Count} 个候选）。手动输入的模型已保留。");
                        break;
                    case AiModelDiscoveryStatus.ModelsUnavailable:
                        SetStatus(SettingsStatusKind.Warning, ModelsUnavailableMessage);
                        break;
                    case AiModelDiscoveryStatus.AuthenticationFailed:
                        SetStatus(SettingsStatusKind.Error, AuthenticationFailedMessage);
                        break;
                    default:
                        SetStatus(SettingsStatusKind.Error, ConnectionUnavailableMessage);
                        break;
                }
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "AiProviderSettings.FetchModels");
                SetStatus(SettingsStatusKind.Error, "获取模型列表失败，请稍后重试。");
            }
            finally
            {
                IsLoadingModels = false;
            }
        }

        /// <summary>发现结果合并进候选列表：绝不删除手动加入 / 已保存的模型；忽略大小写去重。</summary>
        private void MergeDiscoveredModels(IReadOnlyList<string> discovered)
        {
            var merged = false;
            foreach (var id in discovered)
            {
                var normalized = AiProviderModelId.Normalize(id);
                if (normalized is null)
                {
                    continue;
                }

                if (!ModelOptions.Any(existing =>
                        string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    ModelOptions.Add(normalized);
                    merged = true;
                }
            }

            if (merged)
            {
                UpdateDirty();
            }
        }

        private async Task TestConnectionAsync()
        {
            if (_manager is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 配置组件在此环境未启用。");
                return;
            }

            IsTestingConnection = true;
            try
            {
                var draft = BuildDraft();
                var testModel = AiProviderModelId.Normalize(ModelText)
                    ?? AiProviderModelId.Normalize(DefaultModelText);
                var result = await _manager.TestConnectionAsync(
                    draft, GetPlainApiKeyOrNull(), testModel);
                SetStatus(ToStatusKind(result.Status), DescribeConnectionResult(result, testModel));
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "AiProviderSettings.TestConnection");
                SetStatus(SettingsStatusKind.Error, "连接测试失败，请稍后重试。");
            }
            finally
            {
                IsTestingConnection = false;
            }
        }

        private static string DescribeConnectionResult(AiConnectionTestResult result, string? testModel)
        {
            var model = testModel ?? "（未指定）";
            return result.Status switch
            {
                AiConnectionTestStatus.Connected => $"连接成功 · {model}",
                AiConnectionTestStatus.ModelRejected => $"服务可访问，但模型 {model} 不可用。",
                AiConnectionTestStatus.AuthenticationFailed => AuthenticationFailedMessage,
                AiConnectionTestStatus.ConnectionUnavailable => "无法连接到服务。",
                AiConnectionTestStatus.InvalidResponse => "服务响应异常，请检查服务接口是否兼容。",
                AiConnectionTestStatus.MissingModel => "请先选择或输入模型 ID。",
                _ => result.Message
            };
        }

        private async Task TestStructuredOutputAsync()
        {
            if (_manager is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 配置组件在此环境未启用。");
                return;
            }

            IsTestingStructuredOutput = true;
            try
            {
                var draft = BuildDraft();
                var result = await _manager.TestStructuredOutputAsync(
                    draft, GetPlainApiKeyOrNull(), AiProviderModelId.Normalize(ModelText));
                StructuredProbeText = result.Supported
                    ? "支持结构化输出"
                    : "未通过结构化输出测试（不影响 Provider 配置）";
                SetStatus(
                    result.Supported ? SettingsStatusKind.Success : SettingsStatusKind.Warning,
                    result.Supported ? "结构化输出能力测试通过。" : "结构化输出能力测试未通过（可改用 Prompt Only）。管理器返回：" + result.Message);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "AiProviderSettings.TestStructuredOutput");
                SetStatus(SettingsStatusKind.Error, "结构化输出测试失败，请稍后重试。");
            }
            finally
            {
                IsTestingStructuredOutput = false;
            }
        }

        private static SettingsStatusKind ToStatusKind(AiConnectionTestStatus status) =>
            status switch
            {
                AiConnectionTestStatus.Connected => SettingsStatusKind.Success,
                AiConnectionTestStatus.ModelRejected or AiConnectionTestStatus.MissingModel
                    => SettingsStatusKind.Warning,
                _ => SettingsStatusKind.Error
            };

        // ============================================================
        // 保存 / 删除
        // ============================================================

        private async Task SaveAsync()
        {
            if (_manager is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 配置组件在此环境未启用。");
                return;
            }

            var draft = BuildDraft();
            var errors = _manager.ValidateDraft(draft);
            if (errors.Count > 0)
            {
                SetStatus(SettingsStatusKind.Warning, string.Join(" ", errors));
                return;
            }

            IsSaving = true;
            try
            {
                var change = ResolveCredentialChange();
                var result = await _manager.SaveProfileAsync(draft, change);
                if (!result.Success)
                {
                    // 失败：草稿与已持久化状态都保持不变（回滚语义由 manager 保证）。
                    SetStatus(SettingsStatusKind.Error, result.Error ?? "保存失败，请稍后重试。");
                    return;
                }

                _referenceDraft = draft;
                _baselineProfile = draft;
                IsNewDraft = false;
                _apiKeyInput = null;
                _isKeyPendingClear = false;
                IsKeyEditorVisible = false;
                ApiKeyInputReset?.Invoke();
                ReloadProfiles(selectId: draft.Id);
                _ = RefreshCredentialStatusAsync();
                SetStatus(SettingsStatusKind.Success, "Provider 配置已保存。");
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "AiProviderSettings.Save");
                SetStatus(SettingsStatusKind.Error, "保存失败，请稍后重试。");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private async Task DeleteAsync()
        {
            var id = SelectedItem?.ExistingProfileId;
            if (_manager is null || id is null)
            {
                SetStatus(SettingsStatusKind.Warning, "请先选择要删除的 Provider。");
                return;
            }

            var displayName = _manager.GetProfile(id)?.DisplayName ?? id;
            var message = $"删除提供方 {displayName}？关联的本地 API 凭据也会删除。";
            if (string.Equals(id, AiProviderPresets.OllamaPresetId, StringComparison.OrdinalIgnoreCase))
            {
                message += "（这不影响当前诊断使用的旧 Ollama 运行时配置。）";
            }

            if (_confirmationDialog is not null && !_confirmationDialog.Confirm("删除 Provider", message))
            {
                return;
            }

            IsSaving = true;
            try
            {
                var result = await _manager.DeleteProfileAsync(id);
                if (!result.Success)
                {
                    SetStatus(SettingsStatusKind.Error, result.Error ?? "删除失败，请稍后重试。");
                    return;
                }

                ReloadProfiles(selectId: null);
                SetStatus(SettingsStatusKind.Success, "Provider 已删除。");
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "AiProviderSettings.Delete");
                SetStatus(SettingsStatusKind.Error, "删除失败，请稍后重试。");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private void SetStatus(SettingsStatusKind kind, string message)
        {
            StatusKind = kind;
            StatusMessage = message;
        }
    }
}
