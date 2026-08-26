namespace AIGeekTuner.Services.Navigation
{
    public interface INavigationService
    {
        bool CanGoBack { get; }

        /// <summary>
        /// 当前 Frame 正在展示的页面（含 GoBack/Journal 回退后的真实状态）。
        /// 用于长耗时操作完成后判断用户是否仍在发起页面，避免强行拉走用户。
        /// </summary>
        bool IsCurrent(AppPage page);

        void NavigateTo(AppPage page, object? parameter = null);

        void GoBack();
    }
}
