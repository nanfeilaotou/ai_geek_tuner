using System.IO;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording
{
    /// <summary>§41 存储测试：临时目录、原子写、损坏容忍。</summary>
    public class TelemetrySessionStoreTests : IDisposable
    {
        private readonly string _dir;

        public TelemetrySessionStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agt-store-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private static TelemetryRecordingSession Sample(string id) =>
            new(id,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                null,
                2000,
                RecordingStatus.Completed,
                [], [], null, []);

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            var store = new TelemetrySessionStore(_dir);
            var session = Sample("abc") with
            {
                Samples = new List<TelemetrySample>
                {
                    new(1, DateTimeOffset.UtcNow, 12,
                        new List<TelemetryReading>
                        {
                            new(TelemetryMetricKey.CpuPackageTemperature, 71.5,
                                TelemetryUnit.Celsius,
                                TelemetryDeviceIdentity.Cpu("CPU"),
                                TelemetrySourceKind.HwInfo, "raw", null, DateTimeOffset.UtcNow),
                        }),
                }.ToArray(),
            };

            store.Save(session);
            var loaded = store.Load("abc");

            Assert.NotNull(loaded);
            Assert.Equal(session.Id, loaded.Id);
            Assert.Single(loaded.Samples);
            Assert.Equal(71.5, loaded.Samples[0].Readings[0].Value);
            Assert.Equal(TelemetrySourceKind.HwInfo, loaded.Samples[0].Readings[0].Source);
        }

        [Fact]
        public void Save_IsAtomicOverwrite_NoTempLeftBehind()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("id1"));
            store.Save(Sample("id1")); // 覆盖

            var file = store.PathOf("id1");
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(file + ".tmp"));
        }

        [Fact]
        public void Load_Missing_ReturnsNull()
        {
            var store = new TelemetrySessionStore(_dir);
            Assert.Null(store.Load("nope"));
        }

        [Fact]
        public void LoadAll_SkipsCorruptFile_AndReportsIt()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("good"));
            var badDir = Path.Combine(_dir, "bad");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "session.json"), "{ this is not json");

            var sessions = store.LoadAll(out var errors);

            var good = Assert.Single(sessions);
            Assert.Equal("good", good.Id);
            Assert.Contains("bad", errors);
        }

        [Fact]
        public void LoadAll_TolerantToUnknownFutureFields()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("future"));
            var file = store.PathOf("future");
            File.WriteAllText(file,
                File.ReadAllText(file)[..^1] + ",\"SomeFutureField\":123}");

            var sessions = store.LoadAll(out var errors);

            Assert.Empty(errors);
            Assert.Single(sessions);
        }

        [Fact]
        public void LoadAll_OrdersByStartedAtDescending()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("old") with { StartedAtUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) });
            store.Save(Sample("new") with { StartedAtUtc = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero) });

            var sessions = store.LoadAll(out _);

            Assert.Equal("new", sessions[0].Id);
            Assert.Equal("old", sessions[1].Id);
        }

        [Fact]
        public void Delete_RemovesDirectory()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("del"));

            Assert.True(store.Delete("del"));
            Assert.False(store.Delete("del"));
            Assert.Null(store.Load("del"));
        }
    }
}
