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
    }
}
