using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    public interface ITelemetrySessionStore
    {
        /// <summary>原子保存：写临时文件后覆盖式移动。返回最终文件路径。</summary>
        string Save(TelemetryRecordingSession session);

        /// <summary>加载全部会话；损坏的文件被跳过并计入 errors，绝不让页面炸掉。</summary>
        IReadOnlyList<TelemetryRecordingSession> LoadAll(out IReadOnlyList<string> errors);

        TelemetryRecordingSession? Load(string sessionId);

        bool Delete(string sessionId);

        string PathOf(string sessionId);
    }

    /// <summary>
    /// 文件存储：Sessions/{id}/session.json。M2 只记录 canonical 指标，
    /// 一个会话一个 JSON 足够（§10）；不做数据库与多格式。
    /// </summary>
    public sealed class TelemetrySessionStore : ITelemetrySessionStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _rootDirectory;

        public TelemetrySessionStore(string sessionsDirectory)
        {
            _rootDirectory = sessionsDirectory;
            Directory.CreateDirectory(_rootDirectory);
        }

        public TelemetrySessionStore(ApplicationDataPaths paths)
            : this(paths.SessionsDirectory)
        {
        }

        public string PathOf(string sessionId) =>
            Path.Combine(_rootDirectory, sessionId, "session.json");

        public string Save(TelemetryRecordingSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            var directory = Path.Combine(_rootDirectory, session.Id);
            Directory.CreateDirectory(directory);
            var finalPath = Path.Combine(directory, "session.json");
            var tempPath = finalPath + ".tmp";

            File.WriteAllText(tempPath, JsonSerializer.Serialize(session, SerializerOptions));
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }

        public IReadOnlyList<TelemetryRecordingSession> LoadAll(out IReadOnlyList<string> errors)
        {
            var sessions = new List<TelemetryRecordingSession>();
            var errorList = new List<string>();

            foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
            {
                var file = Path.Combine(directory, "session.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var session = JsonSerializer.Deserialize<TelemetryRecordingSession>(
                        File.ReadAllText(file), SerializerOptions);
                    if (session is not null)
                    {
                        sessions.Add(session);
                    }
                }
                catch (Exception exception)
                {
                    // 单个坏文件只记录并跳过（§41）。
                    ExceptionLogWriter.Write(exception, "Telemetry/session load");
                    errorList.Add(Path.GetFileName(directory));
                }
            }

            errors = errorList;
            return sessions.OrderByDescending(session => session.StartedAtUtc).ToList();
        }

        public TelemetryRecordingSession? Load(string sessionId)
        {
            var file = PathOf(sessionId);
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<TelemetryRecordingSession>(
                    File.ReadAllText(file), SerializerOptions);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "Telemetry/session load");
                return null;
            }
        }

        public bool Delete(string sessionId)
        {
            var directory = Path.Combine(_rootDirectory, sessionId);
            if (!Directory.Exists(directory))
            {
                return false;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
    }
}
