using System.Windows;
using System.Windows.Controls;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    public partial class SettingsPage : Page
    {
        private bool _syncingIntervalCombo;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += (_, _) => SyncIntervalFromVm();
            if (DataContext is SettingsViewModel)
            {
                SyncIntervalFromVm();
            }

            DataContextChanged += (_, _) => SyncIntervalFromVm();
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
    }
}
