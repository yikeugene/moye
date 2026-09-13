using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

public sealed class AutosaveTimingTests
{
    [Theory]
    [InlineData(-86400)]
    [InlineData(86400)]
    public async Task WallClockJumpDoesNotMoveContinuousEditDeadline(int clockJumpSeconds)
    {
        var time = new ManualTimeProvider();
        using var repository = new RecordingRepository();
        using var autosave = new AutosaveCoordinator(repository, timeProvider: time);
        var document = new NotebookDocument { Title = "revision 0" };
        autosave.Schedule(document);

        time.Advance(TimeSpan.FromMilliseconds(500));
        var previousUtc = time.GetUtcNow();
        time.ShiftWallClock(TimeSpan.FromSeconds(clockJumpSeconds));
        Assert.Equal(previousUtc.AddSeconds(clockJumpSeconds), time.GetUtcNow());

        for (var revision = 1; revision <= 3; revision++)
        {
            document.Title = $"revision {revision}";
            autosave.Schedule(document);
            Assert.Empty(repository.Saved);
            if (revision < 3) time.Advance(TimeSpan.FromMilliseconds(500));
        }
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Empty(repository.Saved);
        Assert.True(autosave.IsDirty);

        // Exactly two monotonic seconds since the first edit, regardless of the wall-clock correction.
        time.Advance(TimeSpan.FromMilliseconds(1));
        await repository.FirstSave.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("revision 3", Assert.Single(repository.Saved).Title);
        Assert.False(autosave.IsDirty);
        Assert.Null(autosave.LastError);
    }

    [Fact]
    public async Task SuccessfulCommitStartsANewMonotonicWindow()
    {
        var time = new ManualTimeProvider();
        using var repository = new RecordingRepository();
        using var autosave = new AutosaveCoordinator(repository, timeProvider: time);
        var document = new NotebookDocument { Title = "first" };
        autosave.Schedule(document);
        time.Advance(TimeSpan.FromMilliseconds(750));
        await repository.FirstSave.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(repository.Saved);

        time.Advance(TimeSpan.FromHours(1));
        time.ShiftWallClock(TimeSpan.FromDays(-2));
        document.Title = "second";
        autosave.Schedule(document);
        time.Advance(TimeSpan.FromMilliseconds(749));
        Assert.Single(repository.Saved);
        Assert.True(autosave.IsDirty);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new[] { "first", "second" }, repository.Saved.Select(d => d.Title));
        Assert.False(autosave.IsDirty);
    }

    [Fact]
    public async Task FlushingCancelsTheScheduledTimerAndDisposalDoesNotScheduleAnotherWrite()
    {
        var time = new ManualTimeProvider();
        using var repository = new RecordingRepository();
        var autosave = new AutosaveCoordinator(repository, timeProvider: time);
        autosave.Schedule(new NotebookDocument());
        await autosave.FlushAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(repository.Saved);
        autosave.Dispose();
        time.Advance(TimeSpan.FromDays(1));
        Assert.Single(repository.Saved);
    }

    private sealed class RecordingRepository : INotebookRepository
    {
        public List<NotebookDocument> Saved { get; } = [];
        public TaskCompletionSource FirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SaveAsync(NotebookDocument document)
        {
            Saved.Add(document.Snapshot());
            FirstSave.TrySetResult();
            return Task.CompletedTask;
        }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => throw new NotSupportedException();
        public Task<NotebookDocument?> LoadAsync(string id) => throw new NotSupportedException();
        public Task DeleteAsync(string id) => throw new NotSupportedException();
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => throw new NotSupportedException();
        public Task<AssetData> GetAssetAsync(string id) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>UTC can jump independently; timers advance only with the monotonic timestamp.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void ShiftWallClock(TimeSpan delta) => _utcNow += delta;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            var target = checked(_timestamp + elapsed.Ticks);
            while (_timers.Where(t => t.DueTimestamp <= target).OrderBy(t => t.DueTimestamp).FirstOrDefault() is { } timer)
            {
                var due = Math.Max(_timestamp, timer.DueTimestamp);
                _utcNow += TimeSpan.FromTicks(due - _timestamp);
                _timestamp = due;
                timer.Fire();
            }
            _utcNow += TimeSpan.FromTicks(target - _timestamp);
            _timestamp = target;
        }

        private sealed class ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private long _periodTicks;
            public long DueTimestamp { get; private set; } = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(provider._timestamp + dueTime.Ticks);
                _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
            public void Fire()
            {
                DueTimestamp = _periodTicks > 0 ? checked(DueTimestamp + _periodTicks) : long.MaxValue;
                callback(state);
            }
            public void Dispose() { _disposed = true; DueTimestamp = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
