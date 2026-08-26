using System.Windows.Controls;
using System.Windows.Navigation;

namespace AIGeekTuner.Services.Navigation
{
    public sealed class FrameNavigationService : INavigationService
    {
        private readonly Frame _frame;
        private readonly Func<AppPage, object?, Page> _pageFactory;
        private AppPage? _currentPage;

        public FrameNavigationService(
            Frame frame,
            Func<AppPage, object?, Page> pageFactory)
        {
            _frame = frame ?? throw new ArgumentNullException(nameof(frame));
            _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
            // GoBack/Journal 回退不会经过 NavigateTo，
            // 只有监听 Navigated 才能持续跟踪真实当前页。
            _frame.Navigated += OnFrameNavigated;
        }

        public bool CanGoBack => _frame.CanGoBack;

        public bool IsCurrent(AppPage page) => _currentPage == page;

        public void NavigateTo(AppPage page, object? parameter = null)
        {
            var content = _pageFactory(page, parameter);
            content.Tag = page;
            _frame.Navigate(content);
        }

        public void GoBack()
        {
            if (_frame.CanGoBack)
            {
                _frame.GoBack();
            }
        }

        private void OnFrameNavigated(object sender, NavigationEventArgs e)
        {
            _currentPage = e.Content is Page { Tag: AppPage page } ? page : null;
        }
    }
}
