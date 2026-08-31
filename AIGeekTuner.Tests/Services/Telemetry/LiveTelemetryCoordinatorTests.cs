using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>V2-M3.2：LiveTelemetryCoordinator 的共享采样/独立轮询语义回归。</summary>
    public sealed class LiveTelemetryCoordinatorTests
    {
        [Fact]
        public async Task Start_NotRecording_PollsHub_AndPublishes()
        {
            var hub = new FakeHub();
            var coordinator = new LiveTelemetryCoordinator(hub);
            var published = SubscribeOnce(coordinator);

            coordinator.Start(200);

            var finished = await Task.WhenAny(published.Task, Task.Delay(3000));
            Assert.Same(published.Task, finished);
            Assert.True(coordinator.IsRunning);
            Assert.Equal(LiveTelemetrySourceMode.Coordinator, coordinator.Mode);
            Assert.True(hub.ReadCount >= 1);
            coordinator.Stop();
        }

        [Fact]
        public void NotStarted_RecorderSample_IsIgnored()
        {
            var recorder = new FakeRecorder();
            var coordinator = new LiveTelemetryCoordinator(new FakeHub(), recorder);
            var seen = new List<TelemetrySnapshot>();
            coordinator.SnapshotUpdated += s => seen.Add(s);

            recorder.PublishSample(CreateSample(1));

            Assert.False(coordinator.IsRunning);
            Assert.Empty(seen);
            Assert.Null(coordinator.LatestSnapshot);
        }

        [Fact]
        public async Task Running_WhileRecording_MirrorsSample_WithoutExtraHubReads()
        {
            var hub = new FakeHub();
            var recorder = new FakeRecorder();
            var coordinator = new LiveTelemetryCoordinator(hub, recorder);
            var firstHubPoll = SubscribeOnce(coordinator);

            coordinator.Start(200);
            var finished = await Task.WhenAny(firstHubPoll.Task, Task.Delay(3000));
            Assert.Same(firstHubPoll.Task, finished);
            var readsBeforeRecording = hub.ReadCount;

            // 录制开始：镜像推送必须到达，且模式切到 Recorder（§22 禁止双轮询）。
            recorder.IsRecording = true;
            recorder.StartSession(intervalMs: 1000);
            var mirrored = SubscribeOnce(coordinator);
            var sample = CreateSample(7);
            recorder.AddToSession(sample);
            recorder.PublishSample(sample);

            finished = await Task.WhenAny(mirrored.Task, Task.Delay(3000));
            Assert.Same(mirrored.Task, finished);
            Assert.Equal(LiveTelemetrySourceMode.Recorder, coordinator.Mode);
            Assert.Equal(sample.CapturedAtUtc, coordinator.LatestSnapshot!.CapturedAtUtc);

            // 短窗口内 Hub 不得因录制期间出现新的第二套读取。
            await Task.Delay(450);
            Assert.True(hub.ReadCount <= readsBeforeRecording + 3,
                $"Hub reads grew during mirror mode: {readsBeforeRecording} -> {hub.ReadCount}");
            coordinator.Stop();
        }

        [Fact]
        public async Task Stop_AfterRunning_PublishesNoMore()
        {
            var hub = new FakeHub();
            var coordinator = new LiveTelemetryCoordinator(hub);
            var first = SubscribeOnce(coordinator);
            coordinator.Start(200);
            await Task.WhenAny(first.Task, Task.Delay(3000));

            coordinator.Stop();
            var countAfterStop = hub.ReadCount;
            await Task.Delay(450);

            Assert.False(coordinator.IsRunning);
            Assert.Equal(countAfterStop, hub.ReadCount);
        }

        private static TaskCompletionSource<TelemetrySnapshot> SubscribeOnce(
            ILiveTelemetrySource source)
        {
            var tcs = new TaskCompletionSource<TelemetrySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.SnapshotUpdated += s => tcs.TrySetResult(s);
            return tcs;
        }

        private static TelemetrySample CreateSample(int sequence) => new(
            sequence,
            DateTimeOffset.UtcNow,
            ReadDurationMs: 1,
            Readings: []);

        private sealed class FakeHub : ITelemetryHub
        {
            private int _count;
            public int ReadCount => Volatile.Read(ref _count);

            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _count);
                return Task.FromResult(TelemetrySnapshot.Empty(DateTimeOffset.UtcNow));
            }
        }

        private sealed class FakeRecorder : ITelemetryRecordingService
        {
            private TelemetryRecordingSession? _session;

            public bool IsRecording { get; set; }

            public TelemetryRecordingSession? CurrentSession => _session;

            public TelemetrySample? LatestSample { get; private set; }

            public string? LastError => null;

            public string? LastSavedPath => null;

            public event Action<TelemetrySample>? SampleCaptured;

            public void StartSession(int intervalMs)
            {
                _session = TelemetryRecordingSession.Start(intervalMs, DateTimeOffset.UtcNow);
            }

            public void AddToSession(TelemetrySample sample)
            {
                _session?.AddSample(sample);
            }

            public void PublishSample(TelemetrySample sample)
            {
                LatestSample = sample;
                SampleCaptured?.Invoke(sample);
            }

            public bool Start(int intervalMs) => false;

            public Task<TelemetryRecordingSession?> StopAsync() =>
                Task.FromResult<TelemetryRecordingSession?>(null);

            public Task FinalizeIfRecordingAsync(TimeSpan timeout) => Task.CompletedTask;
        }
    }
}