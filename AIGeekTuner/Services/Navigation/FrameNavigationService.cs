using System.Windows.Controls;

namespace AIGeekTuner.Services.Navigation
{
    public sealed class FrameNavigationService : INavigationService
    {
        private readonly Frame _frame;
        private readonly Func<AppPage, object?, Page> _pageFactory;

        public FrameNavigationService(
            Frame frame,
            Func<AppPage, object?, Page> pageFactory)
        {
            _frame = frame;
            _pageFactory = pageFactory;
        }

        public bool CanGoBack => _frame.CanGoBack;

        public void NavigateTo(AppPage page, object? parameter = null)
        {
            _frame.Navigate(_pageFactory(page, parameter));
        }

        public void GoBack()
        {
            if (_frame.CanGoBack)
            {
                _frame.GoBack();
            }
        }
    }
}
