namespace AIGeekTuner.ViewModels
{
    public sealed class DashboardViewModel : ViewModelBase
    {
        public DashboardViewModel(HardwareInfoViewModel hardware)
        {
            Hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public HardwareInfoViewModel Hardware { get; }
    }
}
