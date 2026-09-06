using System.Reflection;

namespace AIGeekTuner.Services.Diagnostics;

/// <summary>Single application-version source for UI and exported metadata.</summary>
public static class ApplicationVersionInfo
{
    public static string Current
    {
        get
        {
            var assembly = typeof(ApplicationVersionInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : informational.Split('+')[0];
        }
    }

    public static string DisplayText => "v" + Current;

    public static string FooterText => DisplayText + " · AI 辅助诊断";
}
