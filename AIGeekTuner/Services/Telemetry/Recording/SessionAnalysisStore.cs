using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    /// <summary>analysis.json 信封：deterministic session.json 保持稳定，AI 结果独立持久化（§24/§25）。</summary>
    public sealed record SessionAnalysisEnvelope(
        int SchemaVersion,
        string SessionId,
        DateTimeOffset AnalyzedAtUtc,
        string ModelName,
        long DurationMs,
        bool RepairUsed,
        SessionAnalysisResult Result,
        string ContextJson);

    public interface ISessionAnalysisStore
    {
        void Save(SessionAnalysisEnvelope envelope);

        SessionAnalysisEnvelope? Load(string sessionId);
    }

    public sealed class SessionAnalysisStore : ISessionAnalysisStore
    {
        private const int SchemaVersion = 1;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _rootDirectory;

        public SessionAnalysisStore(string sessionsDirectory)
        {
            _rootDirectory = sessionsDirectory;
            Directory.CreateDirectory(_rootDirectory);
        }

        public void Save(SessionAnalysisEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var directory = Path.Combine(_rootDirectory, envelope.SessionId);
            Directory.CreateDirectory(directory);
            var wrapped = new EnvelopeFile(SchemaVersion, envelope);
            var temp = Path.Combine(directory, "analysis.json.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(wrapped, SerializerOptions));
            File.Move(temp, Path.Combine(directory, "analysis.json"), overwrite: true);
        }

        public SessionAnalysisEnvelope? Load(string sessionId)
        {
            var file = Path.Combine(_rootDirectory, sessionId, "analysis.json");
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var wrapped = JsonSerializer.Deserialize<EnvelopeFile>(File.ReadAllText(file), SerializerOptions);
                return wrapped?.Analysis;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis load");
                return null;
            }
        }

        private sealed record EnvelopeFile(int SchemaVersion, SessionAnalysisEnvelope Analysis);
    }
}
