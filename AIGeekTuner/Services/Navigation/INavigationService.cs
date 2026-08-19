namespace AIGeekTuner.Services.Navigation
{
    public interface INavigationService
    {
        bool CanGoBack { get; }

        void NavigateTo(AppPage page, object? parameter = null);

        void GoBack();
    }
}
