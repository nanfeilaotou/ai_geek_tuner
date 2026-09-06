using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    public partial class SettingsPage : Page
    {
        private bool _syncingIntervalCombo;
        private bool _syncingProviderKeyBox;
        private AiProviderSettingsViewModel? _providersViewModel;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                SyncIntervalFromVm();
                BindProviderKeyBox();
            };
            DataContextChanged += (_, _) =>
            {
                SyncIntervalFromVm();
                BindProviderKeyBox();
            };
        }

        // V2-M5.1A：Provider API Key 输入框的生命周期。
        // 明文只在 PasswordBox 与 VM 瞬时字段之间单向流动；
        // VM 要求清空（切换/保存/清除后）时通过事件回清控件，绝不回显已存密钥。
        private void BindProviderKeyBox()
        {
            if (_providersViewModel is not null)
            {
                _providersViewModel.ApiKeyInputReset -= ClearProviderApiKeyBox;
            }

            _providersViewModel = (DataContext as SettingsViewModel)?.Providers;
            if (_providersViewModel is not null)
            {
                _providersViewModel.ApiKeyInputReset += ClearProviderApiKeyBox;
            }
        }

        private void ClearProviderApiKeyBox()
        {
            _syncingProviderKeyBox = true;
            try
            {
                ProviderApiKeyBox.Clear();
            }
            finally
            {
                _syncingProviderKeyBox = false;
            }
        }

        private void ProviderApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingProviderKeyBox)
            {
                return;
            }

            (DataContext as SettingsViewModel)?.Providers?.SetApiKeyInput(ProviderApiKeyBox.Password);
        }

        private void ProviderBeginKeyUpdate_Click(object sender, RoutedEventArgs e)
        {
            ClearProviderApiKeyBox();
            ProviderApiKeyBox.Focus();
        }

        // V2-M5.1A.1：“＋ 添加 Provider”紧凑菜单入口（预设选项见菜单项，命令在 ViewModel）。
        private void AddProviderButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void SyncIntervalFromVm()
        {
            if (DataContext is not SettingsViewModel viewModel)
            {
                return;
            }

            _syncingIntervalCombo = true;
            foreach (ComboBoxItem item in RecordingIntervalCombo.Items)
            {
                if (item.Tag?.ToString() == viewModel.RecordingIntervalMs.ToString())
                {
                    RecordingIntervalCombo.SelectedItem = item;
                    break;
                }
            }

            _syncingIntervalCombo = false;
        }

        private void RecordingInterval_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_syncingIntervalCombo)
            {
                return;
            }

            if (DataContext is SettingsViewModel viewModel
                && RecordingIntervalCombo.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out var interval))
            {
                viewModel.RecordingIntervalMs = interval;
            }
        }

        // M5.2G：文本/数字草稿在失焦时立即尝试提交（合法 → 自动保存；
        // 非法 → 保持草稿与错误状态，不覆盖最后有效值）。
        private void SettingsInput_LostKeyboardFocus(
            object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox)
            {
                _ = (DataContext as SettingsViewModel)?.CommitAutoSaveAsync();
            }
        }

        private void SettingsInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox)
            {
                _ = (DataContext as SettingsViewModel)?.CommitAutoSaveAsync();
            }
        }
    }
}