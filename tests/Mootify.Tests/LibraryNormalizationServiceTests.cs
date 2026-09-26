using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Transcoding;

namespace Mootify.Tests;

public sealed class LibraryNormalizationServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private LibraryNormalizationService _service = null!;
    private TranscodeCache _cache = null!;
    private string _root = null!;
    private Guid _adminId;

    public async Task InitializeAsync()
    {
        _db = new TestDatabase();
        _root = Path.Combine(Path.GetTempPath(), "mootify-normalization-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        var admin = await _db.AddUserAsync("admin");
        _adminId = admin.Id;
        await using var db = _db.CreateDbContext();
        await db.Users.Where(u => u.Id == _adminId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, true));
        var options = new StaticOptionsMonitor<TranscodeOptions>(new TranscodeOptions
        {
            CacheDirectory = Path.Combine(_root, "cache"),
            FfmpegPath = "mootify-test-no-ffmpeg",
        });
        var transcoder = new Transcoder(options, NullLogger<Transcoder>.Instance);
        _cache = new TranscodeCache(transcoder, options, new StaticOptionsMonitor<LibraryOptions>(new LibraryOptions { MusicRoot = _root }), NullLogger<TranscodeCache>.Instance);
        _service = new LibraryNormalizationService(_db, _cache, transcoder, NullLogger<LibraryNormalizationService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<List<Guid>> SeedAsync(params (string Extension, bool Present, bool Cached)[] specifications)
    {
        var ids = await _db.AddTracksAsync(specifications.Length);
        await using var db = _db.CreateDbContext();
        for (var i = 0; i < ids.Count; i++)
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == ids[i]);
            track.Path = Path.Combine(_root, ids[i] + specifications[i].Extension);
            track.IsPresent = specifications[i].Present;
            File.WriteAllText(track.Path, "original");
            File.SetLastWriteTimeUtc(track.Path, DateTime.UtcNow.AddHours(-2));
            if (specifications[i].Cached)
            {
                Directory.CreateDirectory(_cache.NormalizedDirectory);
                File.WriteAllText(CachedPath(ids[i]), "normalized copy");
            }
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private string CachedPath(Guid id) => Path.Combine(_cache.NormalizedDirectory, $"{id:n}-normalized-v1.mp3");

    private Task<NormalizationProgress> Completion()
    {
        var completion = new TaskCompletionSource<NormalizationProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            var progress = _service.Progress;
            if (progress.IsRunning || progress.FinishedAt is null) return;
            _service.Changed -= Changed;
            completion.TrySetResult(progress);
        }
        _service.Changed += Changed;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Rejects_parallelism_outside_slider_range(int parallelism)
    {
        Assert.False((await _service.PrepareAsync(_adminId, parallelism: parallelism)).Ok);
        Assert.False(_service.Progress.IsRunning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Runs_selected_number_of_workers_and_cancels_them(int parallelism)
    {
        await SeedAsync(Enumerable.Repeat((".mp3", true, false), 16).ToArray());
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        void BlockWorkers()
        {
            if (_service.Progress.Current is null || release.IsSet) return;
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
            if (count == parallelism) started.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Decrement(ref active);
        }
        _service.Changed += BlockWorkers;
        await _service.StartAsync(CancellationToken.None);
        var done = Completion();
        try
        {
            Assert.True((await _service.PrepareAsync(_adminId, parallelism: parallelism)).Ok);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(parallelism, peak);
            Assert.Equal(parallelism, _service.Progress.Parallelism);
            _service.Changed -= BlockWorkers;
            Assert.True((await _service.CancelAsync(_adminId)).Ok);
        }
        finally
        {
            _service.Changed -= BlockWorkers;
            release.Set();
        }
        var progress = await done;
        Assert.True(progress.Cancelled);
        Assert.Null(progress.Current);
        Assert.False(progress.IsRunning);
    }

    [Fact]
    public async Task Parallel_cache_hits_have_exact_progress_counts()
    {
        await SeedAsync(Enumerable.Repeat((".mp3", true, true), 32).ToArray());
        await _service.StartAsync(CancellationToken.None);
        var done = Completion();
        Assert.True((await _service.PrepareAsync(_adminId, parallelism: 8)).Ok);
        var progress = await done;
        Assert.Equal(32, progress.AlreadyReady);
        Assert.Equal(100, progress.Percent);
        Assert.Equal(0, progress.Failed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Repeated_failures_stop_scheduling_more_tracks(int parallelism)
    {
        await SeedAsync(Enumerable.Repeat((".mp3", true, false), 32).ToArray());
        await _service.StartAsync(CancellationToken.None);
        var done = Completion();
        Assert.True((await _service.PrepareAsync(_adminId, parallelism: parallelism)).Ok);
        var progress = await done;
        Assert.InRange(progress.Failed, 5, 5 + parallelism - 1);
        Assert.Contains("Stopped after five consecutive failures", progress.LastError);
        Assert.False(progress.Cancelled);
    }

    [Fact]
    public async Task Prepares_only_present_mp3s_and_reuses_playback_cache_on_rerun()
    {
        var ids = await SeedAsync((".mp3", true, true), (".MP3", true, true), (".flac", true, false), (".mp3", false, false));
        await _service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 2; i++)
        {
            var done = Completion();
            Assert.True((await _service.PrepareAsync(_adminId)).Ok);
            var progress = await done;
            Assert.Equal(2, progress.Total);
            Assert.Equal(2, progress.AlreadyReady);
            Assert.Equal(0, progress.Prepared);
            Assert.Equal(0, progress.Failed);
            Assert.Equal(100, progress.Percent);
            Assert.Null(progress.LastError);
        }
        await using var db = _db.CreateDbContext();
        var track = await db.Tracks.SingleAsync(t => t.Id == ids[0]);
        Assert.Equal(CachedPath(ids[0]), await _cache.GetOrCreateAsync(track.Id, track.Path, normalize: true));
        Assert.Equal("original", File.ReadAllText(track.Path));
    }

    [Fact]
    public async Task Only_active_approved_admins_can_start_or_cancel_and_runs_do_not_overlap()
    {
        var user = await _db.AddUserAsync("user");
        Assert.False((await _service.PrepareAsync(user.Id)).Ok);
        Assert.True((await _service.PrepareAsync(_adminId)).Ok);
        Assert.True(_service.Progress.IsRunning);
        Assert.False((await _service.PrepareAsync(_adminId)).Ok);
        Assert.False((await _service.CancelAsync(user.Id)).Ok);
        Assert.True((await _service.CancelAsync(_adminId)).Ok);
        var done = Completion();
        await _service.StartAsync(CancellationToken.None);
        Assert.True((await done).Cancelled);
        await using var db = _db.CreateDbContext();
        await db.Users.Where(u => u.Id == _adminId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ApprovalPending, true));
        Assert.False((await _service.PrepareAsync(_adminId)).Ok);
        await db.Users.Where(u => u.Id == _adminId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ApprovalPending, false).SetProperty(u => u.IsBanned, true));
        Assert.False((await _service.PrepareAsync(_adminId)).Ok);
    }

    [Fact]
    public async Task Stale_and_missing_copies_are_attempted_and_errors_are_reported_without_changing_originals()
    {
        var ids = await SeedAsync((".mp3", true, true), (".mp3", true, false));
        File.SetLastWriteTimeUtc(CachedPath(ids[0]), DateTime.UtcNow.AddDays(-1));
        await _service.StartAsync(CancellationToken.None);
        var done = Completion();
        Assert.True((await _service.PrepareAsync(_adminId)).Ok);
        var progress = await done;
        Assert.Equal(2, progress.Failed);
        Assert.Equal(0, progress.AlreadyReady);
        Assert.NotNull(progress.LastError);
        foreach (var file in Directory.GetFiles(_root, "*.mp3")) Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public async Task Empty_library_completes_and_disconnected_progress_subscriber_does_not_break_job()
    {
        _service.Changed += () => throw new InvalidOperationException("Disconnected UI");
        await _service.StartAsync(CancellationToken.None);
        var done = Completion();
        Assert.True((await _service.PrepareAsync(_adminId)).Ok);
        var progress = await done;
        Assert.Equal(0, progress.Total);
        Assert.Null(progress.LastError);
        Assert.False(progress.Cancelled);
    }

    [Fact]
    public async Task Empty_and_stale_cache_files_are_not_ready()
    {
        var ids = await SeedAsync((".mp3", true, true));
        var source = Path.Combine(_root, ids[0] + ".mp3");
        Assert.NotNull(_cache.FindReady(ids[0], source, normalize: true));
        Assert.Null(_cache.FindReady(ids[0], source));
        File.WriteAllText(CachedPath(ids[0]), "");
        Assert.Null(_cache.FindReady(ids[0], source, normalize: true));
        File.WriteAllText(CachedPath(ids[0]), "stale");
        File.SetLastWriteTimeUtc(CachedPath(ids[0]), DateTime.UtcNow.AddDays(-1));
        Assert.Null(_cache.FindReady(ids[0], source, normalize: true));
    }

    [Fact]
    public async Task Legacy_normalized_copy_moves_to_music_storage_without_reencoding()
    {
        var ids = await SeedAsync((".mp3", true, false));
        Directory.CreateDirectory(_cache.CacheDirectory);
        var legacy = Path.Combine(_cache.CacheDirectory, $"{ids[0]:n}-normalized-v1.mp3");
        await File.WriteAllTextAsync(legacy, "already normalized");
        var result = await _cache.GetOrCreateAsync(ids[0], Path.Combine(_root, ids[0] + ".mp3"), normalize: true);
        Assert.Equal(Path.Combine(_root, "Cache", "Normalized", $"{ids[0]:n}-normalized-v1.mp3"), result);
        Assert.Equal("already normalized", await File.ReadAllTextAsync(result!));
        Assert.False(File.Exists(legacy));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_root, ids[0] + ".mp3")));
    }
}
