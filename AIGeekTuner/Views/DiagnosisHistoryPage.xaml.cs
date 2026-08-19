using System.Windows;
using System.Windows.Controls;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    public partial class DiagnosisHistoryPage : Page
    {
        public DiagnosisHistoryPage()
        {
            InitializeComponent();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DiagnosisHistoryViewModel viewModel)
            {
                return;
            }

            await viewModel.LoadAsync();
        }
    }
}
