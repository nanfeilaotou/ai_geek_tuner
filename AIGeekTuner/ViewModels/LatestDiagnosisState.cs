using AIGeekTuner.Models;

namespace AIGeekTuner.ViewModels
{
    public sealed class LatestDiagnosisState : ViewModelBase
    {
        private DiagnosisOutcome? _outcome;

        public DiagnosisOutcome? Outcome
        {
            get => _outcome;
            set => SetProperty(ref _outcome, value);
        }
    }
}
