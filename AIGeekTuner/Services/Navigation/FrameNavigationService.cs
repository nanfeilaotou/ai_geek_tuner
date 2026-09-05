using System.Windows.Controls;
using System.Windows;
using System.Windows.Navigation;

namespace AIGeekTuner.Services.Navigation
{
    public sealed class FrameNavigationService : INavigationService, IPageNavigationNotifications
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

        public event Action<AppPage>? CurrentPageChanged;

        public bool IsCurrent(AppPage page) => _currentPage == page;

        public bool IsCurrentDataContext(object dataContext)
        {
            ArgumentNullException.ThrowIfNull(dataContext);
            return _frame.Content is FrameworkElement element
                && ReferenceEquals(element.DataContext, dataContext);
        }

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
            if (e.Content is Page { Tag: AppPage page })
            {
                _currentPage = page;
                CurrentPageChanged?.Invoke(page);
            }
            else
            {
                _currentPage = null;
            }
        }
    }
}
