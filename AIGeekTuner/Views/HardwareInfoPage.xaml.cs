using System.Windows;
using System.Windows.Controls;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    public partial class HardwareInfoPage : Page
    {
        public HardwareInfoPage()
        {
            InitializeComponent();
        }

        private async void OnLoaded(
            object sender,
            System.Windows.RoutedEventArgs e)
        {
            if (DataContext is HardwareInfoViewModel viewModel)
            {
                await viewModel.InitializeSensorsAsync();
            }
        }

        private void OnShowDataSourcesDetail(
            object sender,
            System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not HardwareInfoViewModel viewModel
                || viewModel.LastDebugRows.Count == 0)
            {
                return;
            }

            var dialog = new DataSourcesDetailWindow(
                viewModel.LastDebugRows,
                viewModel.DebugVersions,
                viewModel.SensorCapturedAt)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.Show();
        }
    }
}
