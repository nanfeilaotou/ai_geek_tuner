namespace AIGeekTuner.Services.Navigation
{
    /// <summary>
    /// Optional notification seam for hosts that can observe the actual Frame page.
    /// It keeps shell display state synchronized for programmatic navigation too.
    /// </summary>
    public interface IPageNavigationNotifications
    {
        event Action<AppPage>? CurrentPageChanged;
    }

    public interface INavigationService
    {
        bool CanGoBack { get; }

        /// <summary>
        /// 当前 Frame 正在展示的页面（含 GoBack/Journal 回退后的真实状态）。
        /// 用于长耗时操作完成后判断用户是否仍在发起页面，避免强行拉走用户。
        /// </summary>
        bool IsCurrent(AppPage page);

        /// <summary>
        /// 判断当前 Frame 是否仍展示同一个页面 DataContext 实例。
        /// 未提供实例能力的 host 默认拒绝声明 ownership；WPF 实现使用 reference identity。
        /// </summary>
        bool IsCurrentDataContext(object dataContext) =>
            false;

        void NavigateTo(AppPage page, object? parameter = null);

        void GoBack();
    }
}
