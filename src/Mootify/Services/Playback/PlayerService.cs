using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Playlists;
using Mootify.Services.Recommendations;
using Mootify.Services.Settings;

namespace Mootify.Services.Playback;

public sealed record TrackInfo(
    Guid Id,
    string Title,
    string ArtistName,
    string AlbumTitle,
    Guid AlbumId,
    TimeSpan Duration,
    /// <summary>
    /// True when the file isn't an MP3, so the browser is offered a transcoded fallback in
    /// case it can't decode the original. The path itself never leaves the server.
    /// </summary>
    bool NeedsFallback = false,
    /// <summary>"FLAC", "MP3" — what the file on disk is. Shown in the play bar.</summary>
    string Format = "MP3")
{
    /// <summary>Extension to something worth putting on screen. The path never leaves the server.</summary>
    public static string FormatOf(string path) =>
        Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "AUDIO";
}

/// <summary>
/// One row of the queue drawer. <paramref name="OrderIndex"/> is a position in the *play
/// order*, not in the queue: with shuffle on those are different numbers, and the play order
/// is the one a jump has to be expressed in.
/// </summary>
public sealed record QueueEntry(int OrderIndex, TrackInfo Track);

/// <summary>
/// Owns playback state for one circuit. The play bar and every in-list play button render
/// from this — two components each holding their own "is playing" flag is how you get a
/// pause button that lies.
/// </summary>
public sealed class PlayerService(
    IJSRuntime js,
    IDbContextFactory<MootifyDbContext> dbFactory,
    ListeningService listening,
    TasteService taste,
    PreferenceService preferences,
    CurrentUser currentUser,
    ILogger<PlayerService> log) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlayerService>? _selfRef;

    /// <summary>When the last listening heartbeat went out. See <see cref="BroadcastAsync"/>.</summary>
    private DateTimeOffset _lastBroadcast = DateTimeOffset.MinValue;

    /// <summary>
    /// The track seconds are being counted against, and how far it had got when we last looked.
    ///
    /// Kept separately from <see cref="Current"/> because by the time a track change is handled,
    /// <see cref="Position"/> already belongs to the next song — the same reason the Android
    /// reporter keeps its own copy.
    /// </summary>
    private Guid? _countingTrackId;
    private double _countedSeconds;

    /// <summary>
    /// Set when the queue came from "Surprise me". Such a queue always tops itself up, whatever
    /// the auto-continue preference says — endlessness is the thing that was asked for, not a
    /// side effect of a setting.
    /// </summary>
    private bool _alwaysGrow;

    /// <summary>Stops two top-ups racing when the last track ends while one is already running.</summary>
    private bool _growing;

    private List<TrackInfo> _queue = [];

    /// <summary>Play order as indices into <see cref="_queue"/>. Natural order, or a seeded permutation.</summary>
    private List<int> _order = [];
    private int _cursor = -1;

    public IReadOnlyList<TrackInfo> Queue => _queue;
    public TrackInfo? Current { get; private set; }
    public bool IsPlaying { get; private set; }
    public double Position { get; private set; }
    public double Duration { get; private set; }
    public double Volume { get; private set; } = 0.8;
    public bool ShuffleEnabled { get; private set; }
    public RepeatMode Repeat { get; private set; } = RepeatMode.Off;
    public int ShuffleSeed { get; private set; } = Random.Shared.Next();

    /// <summary>
    /// Set when the browser couldn't decode the original and took the transcoded copy. The
    /// play bar shows what's actually coming down the wire, not what's on disk — otherwise
    /// it would claim FLAC while streaming a 320k MP3.
    /// </summary>
    public bool UsingFallback { get; private set; }

    /// <summary>What's actually playing right now, for the play bar badge.</summary>
    public string? PlayingFormat => Current is null ? null : UsingFallback ? "MP3" : Current.Format;

    public bool HasNext => _cursor >= 0 && (_cursor + 1 < _order.Count || Repeat == RepeatMode.All);
    public bool HasPrevious => _cursor > 0 || Position > 3;

    /// <summary>
    /// The playlist this queue was started from, or null when it came from an album, an artist or
    /// a search. It is the only thing that makes "listening along" addressable — a queue with no
    /// source has no set of people who are entitled to see it, so nothing is broadcast.
    ///
    /// Set by whoever built the queue and then left alone. Enqueueing more tracks onto a playlist
    /// queue keeps the source: the person is still working through that list, and dropping the
    /// claim on the first added track would make the strip flicker for everybody watching.
    /// </summary>
    public Guid? SourcePlaylistId { get; private set; }

    public event Func<Task>? StateChanged;

    private async Task NotifyAsync()
    {
        if (StateChanged is not null)
        {
            await StateChanged.Invoke();
        }
    }

    public async Task EnsureInitializedAsync()
    {
        if (_module is not null) return;

        _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/player.js");
        _selfRef = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("init", _selfRef);
        await _module.InvokeVoidAsync("setVolume", Volume);
    }

    // ---- queue -----------------------------------------------------------

    public async Task PlayTrackAsync(Guid trackId) => await PlayQueueAsync([trackId], 0);

    /// <summary>
    /// Whether this is the track the player is on right now. Every list renders its play control
    /// from this rather than comparing ids itself — the row button showed a pause icon and then
    /// restarted the song, because the icon and the click handler were two expressions of the
    /// same idea and only one of them knew about it.
    /// </summary>
    public bool IsCurrent(Guid trackId) => Current?.Id == trackId;

    /// <summary>
    /// Pressing the play control on a row in a list.
    ///
    /// On the track that is already playing this is a <b>pause</b>, and on a paused one a resume:
    /// the button is showing a pause icon at that point, and re-queueing would jump back to 0:00,
    /// which is never what somebody pressing a pause icon means. Any other row starts the list
    /// from there.
    ///
    /// Here rather than in the component because it is a decision about playback state, and this
    /// is the thing that owns playback state.
    ///
    /// <paramref name="alwaysGrow"/> rides through to <see cref="PlayQueueAsync"/> so a list that
    /// is endless by nature stays endless whichever control started it — pressing row 4 of a
    /// surprise run means the same thing as pressing Play above it.
    /// </summary>
    public async Task PressRowAsync(
        IReadOnlyList<Guid> trackIds,
        int index,
        Guid? sourcePlaylistId = null,
        bool alwaysGrow = false)
    {
        if (index < 0 || index >= trackIds.Count) return;

        if (IsCurrent(trackIds[index]))
        {
            await TogglePlayPauseAsync();
            return;
        }

        await PlayQueueAsync(trackIds, index, sourcePlaylistId, alwaysGrow);
    }

    /// <summary>
    /// Starts a queue. <paramref name="sourcePlaylistId"/> is what the tracks were picked out of,
    /// when that was a playlist — see <see cref="SourcePlaylistId"/>.
    /// </summary>
    public async Task PlayQueueAsync(
        IReadOnlyList<Guid> trackIds,
        int startIndex,
        Guid? sourcePlaylistId = null,
        bool alwaysGrow = false)
    {
        if (trackIds.Count == 0) return;

        // Before the queue is replaced: whatever was playing has now been left, and its seconds
        // are only reportable while we still know what they belong to.
        await FlushPlayAsync();

        _queue = await LoadTracksAsync(trackIds);
        if (_queue.Count == 0) return;

        SourcePlaylistId = sourcePlaylistId;
        _alwaysGrow = alwaysGrow;

        startIndex = Math.Clamp(startIndex, 0, _queue.Count - 1);
        BuildOrder(startAt: startIndex);
        await PlayAtCursorAsync();
    }

    /// <summary>Append without disturbing what's playing or the shuffle permutation already walked.</summary>
    public async Task EnqueueAsync(IReadOnlyList<Guid> trackIds)
    {
        var added = await LoadTracksAsync(trackIds);
        if (added.Count == 0) return;

        var firstNew = _queue.Count;
        _queue.AddRange(added);

        for (var i = firstNew; i < _queue.Count; i++)
        {
            _order.Add(i);
        }

        if (Current is null)
        {
            _cursor = 0;
            await PlayAtCursorAsync();
        }
        else
        {
            await NotifyAsync();
        }
    }

    /// <summary>
    /// Seeded Fisher-Yates, generated once. Storing the seed and walking the permutation means
    /// no repeats until the queue is exhausted, and a stable "up next" list across reloads.
    /// </summary>
    private void BuildOrder(int startAt)
    {
        _order = [.. Enumerable.Range(0, _queue.Count)];

        if (ShuffleEnabled)
        {
            var rng = new Random(ShuffleSeed);
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }

            // Keep the track the user actually clicked first.
            var pos = _order.IndexOf(startAt);
            if (pos > 0)
            {
                (_order[0], _order[pos]) = (_order[pos], _order[0]);
            }

            _cursor = 0;
        }
        else
        {
            _cursor = startAt;
        }
    }

    private async Task<List<TrackInfo>> LoadTracksAsync(IReadOnlyList<Guid> ids)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var rows = await db.Tracks
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.IsPresent)
            .Select(t => new
            {
                t.Id,
                t.Title,
                ArtistName = t.Artist!.Name,
                AlbumTitle = t.Album!.Title,
                t.AlbumId,
                t.DurationTicks,
                t.Path,
            })
            .ToListAsync();

        // Preserve the caller's order; the IN query returns whatever the database felt like.
        var byId = rows.ToDictionary(
            r => r.Id,
            r => new TrackInfo(
                r.Id, r.Title, r.ArtistName, r.AlbumTitle, r.AlbumId, TimeSpan.FromTicks(r.DurationTicks),
                NeedsFallback: !r.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase),
                Format: TrackInfo.FormatOf(r.Path)));
        return [.. ids.Select(id => byId.GetValueOrDefault(id)).OfType<TrackInfo>()];
    }

    // ---- transport -------------------------------------------------------

    private async Task PlayAtCursorAsync()
    {
        if (_cursor < 0 || _cursor >= _order.Count)
        {
            return;
        }

        // The track we are leaving, before Current moves off it.
        await FlushPlayAsync();

        Current = _queue[_order[_cursor]];
        Position = 0;
        Duration = Current.Duration.TotalSeconds;
        IsPlaying = true;

        _countingTrackId = Current.Id;
        _countedSeconds = 0;

        // Each track gets a fresh verdict; the last one falling back says nothing about this one.
        UsingFallback = false;

        await EnsureInitializedAsync();

        // The second URL is only offered for non-MP3 sources; player.js uses it when the
        // browser says it can't decode the original, or when a native decode fails anyway.
        await _module!.InvokeVoidAsync(
            "play",
            $"/media/{Current.Id}",
            Current.NeedsFallback ? $"/media/{Current.Id}/mp3" : null);

        // A new track is the moment the broadcast is most wrong, so it goes out immediately
        // rather than waiting for the next heartbeat.
        await BroadcastAsync(force: true);

        await NotifyAsync();
    }

    public async Task TogglePlayPauseAsync()
    {
        if (Current is null)
        {
            return;
        }

        await EnsureInitializedAsync();
        IsPlaying = !IsPlaying;
        await _module!.InvokeVoidAsync(IsPlaying ? "resume" : "pause");
        await BroadcastAsync(force: true);
        await NotifyAsync();
    }

    public async Task NextAsync()
    {
        if (_order.Count == 0) return;

        if (Repeat == RepeatMode.One)
        {
            // Seeking alone isn't enough: this is normally reached from the `ended` event,
            // and rewinding an element that has finished doesn't restart it. Resume
            // unconditionally — play() on an already-playing element is a no-op.
            await SeekAsync(0);
            IsPlaying = true;
            await _module!.InvokeVoidAsync("resume");
            await NotifyAsync();
            return;
        }

        if (_cursor + 1 < _order.Count)
        {
            _cursor++;
        }
        else if (Repeat == RepeatMode.All)
        {
            _cursor = 0;
        }
        // The end of the queue, and the one moment worth asking for more. Repeat.All never gets
        // here, which is right: somebody looping a playlist has said what they want to hear.
        else if (await TryGrowQueueAsync())
        {
            _cursor++;
        }
        else
        {
            await FlushPlayAsync();
            IsPlaying = false;
            await EnsureInitializedAsync();
            await _module!.InvokeVoidAsync("pause");
            await NotifyAsync();
            return;
        }

        await PlayAtCursorAsync();
    }

    public async Task PreviousAsync()
    {
        // Restart the current track first, like every other player does.
        if (Position > 3 || _cursor <= 0)
        {
            await SeekAsync(0);
            return;
        }

        _cursor--;
        await PlayAtCursorAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        await EnsureInitializedAsync();
        Position = seconds;
        await _module!.InvokeVoidAsync("seek", seconds);
        await NotifyAsync();
    }

    public async Task SetVolumeAsync(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        await EnsureInitializedAsync();
        await _module!.InvokeVoidAsync("setVolume", Volume);
        await NotifyAsync();
    }

    public async Task ToggleShuffleAsync()
    {
        ShuffleEnabled = !ShuffleEnabled;

        if (_queue.Count > 0 && _cursor >= 0 && _cursor < _order.Count)
        {
            var currentIndex = _order[_cursor];

            if (ShuffleEnabled)
            {
                ShuffleSeed = Random.Shared.Next();
                BuildOrder(currentIndex);
            }
            else
            {
                // Turning shuffle off resumes the natural order at the current track
                // rather than jumping somewhere else.
                _order = [.. Enumerable.Range(0, _queue.Count)];
                _cursor = currentIndex;
            }
        }

        await NotifyAsync();
    }

    public async Task CycleRepeatAsync()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        await NotifyAsync();
    }

    // ---- queue preview ---------------------------------------------------
    //
    // The drawer walks _order, never _queue. Shuffle is a seeded permutation generated once
    // and then walked, so the next N positions in _order *are* the next N tracks — a preview
    // that listed the queue array instead would be confidently wrong the moment shuffle is on,
    // which is exactly when somebody wants to look at it.

    /// <summary>How many tracks are still to come, so the drawer can own up to what it cut off.</summary>
    public int UpNextTotal() => UpNextTotal(_order.Count, _cursor, Repeat);

    /// <summary>Up next, in play order, for the queue drawer.</summary>
    public IReadOnlyList<QueueEntry> UpNext(int take = 50) =>
    [
        .. UpNextPositions(_order.Count, _cursor, Repeat, take)
            .Select(pos => new QueueEntry(pos, _queue[_order[pos]]))
    ];

    /// <summary>
    /// Pure, so the awkward cases have tests rather than a drawer somebody eyeballs once.
    ///
    /// <see cref="RepeatMode.All"/> wraps back to the top and stops one short of the current
    /// track: the queue really does continue there, and listing the song you're on as "up
    /// next" would make the preview a liar in the other direction. <see cref="RepeatMode.One"/>
    /// changes what the `ended` event does, not what is queued — pressing next still walks on
    /// — so the list is unchanged and the drawer says "repeating this track" in words.
    /// </summary>
    public static int UpNextTotal(int orderCount, int cursor, RepeatMode repeat)
    {
        if (orderCount <= 0 || cursor < 0) return 0;

        return Math.Max(0, repeat == RepeatMode.All ? orderCount - 1 : orderCount - 1 - cursor);
    }

    /// <summary>Positions in the play order that come after the cursor. See <see cref="UpNextTotal"/>.</summary>
    public static IReadOnlyList<int> UpNextPositions(int orderCount, int cursor, RepeatMode repeat, int take)
    {
        if (take <= 0) return [];

        var count = Math.Min(take, UpNextTotal(orderCount, cursor, repeat));
        if (count <= 0) return [];

        var positions = new List<int>(count);
        for (var i = 1; i <= count; i++)
        {
            positions.Add((cursor + i) % orderCount);
        }

        return positions;
    }

    /// <summary>
    /// Play a position in the play order — a row click in the queue drawer. It moves the
    /// cursor rather than re-queueing, so everything behind and ahead of it survives, and
    /// with shuffle on the rest of the permutation is still the one being previewed.
    /// </summary>
    public async Task JumpToAsync(int orderIndex)
    {
        if (orderIndex < 0 || orderIndex >= _order.Count) return;

        _cursor = orderIndex;
        await PlayAtCursorAsync();
    }

    // ---- play history ----------------------------------------------------

    /// <summary>
    /// Writes down the track just left and how far it got.
    ///
    /// The website did not record this at all — <c>PlayEvent</c> was written only by the Android
    /// app — which meant anybody who listens on the website had no history, and everything built
    /// on history had nothing to work from. It is the same shape as the app's
    /// <c>PlaybackReporter</c>: one event per track, on leaving it, carrying the seconds played,
    /// because that is what tells a listen from a skip.
    ///
    /// Failures are swallowed. This is bookkeeping for the listener's own benefit and never a
    /// reason for their music to stop.
    /// </summary>
    private async Task FlushPlayAsync()
    {
        if (_countingTrackId is not { } trackId) return;

        // Position still belongs to this track at every call site, but take the larger of the two
        // so a pause that happened before the flush isn't lost.
        var seconds = Math.Max(_countedSeconds, Current?.Id == trackId ? Position : 0);

        _countingTrackId = null;
        _countedSeconds = 0;

        if (seconds < MinimumReportableSeconds) return;

        try
        {
            if (await currentUser.GetIdAsync() is not { } userId) return;

            await using var db = await dbFactory.CreateDbContextAsync();

            db.PlayEvents.Add(new PlayEvent
            {
                UserId = userId,
                TrackId = trackId,
                PlayedAt = DateTimeOffset.UtcNow,
                SecondsPlayed = seconds,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not record a play of {Track}", trackId);
        }
    }

    /// <summary>
    /// Below this it was a skip, and the play history is better off without it. The same number
    /// the Android reporter uses, so the two clients agree about what counts as listening.
    /// </summary>
    private const double MinimumReportableSeconds = 5;

    // ---- running out -----------------------------------------------------

    /// <summary>
    /// The queue is finished. Top it up from what this account listens to, when that has been
    /// asked for and when there is enough history to do it without guessing.
    ///
    /// Returns whether anything was added, so the caller can carry on playing rather than stop.
    /// </summary>
    private async Task<bool> TryGrowQueueAsync()
    {
        if (_growing) return false;

        try
        {
            _growing = true;

            if (await currentUser.GetIdAsync() is not { } userId) return false;
            if (!_alwaysGrow && !await preferences.GetAutoContinueAsync(userId)) return false;

            // Nothing already in the queue comes back round; TasteService also holds out anything
            // heard recently, which after a long session is most of this anyway.
            var suggestions = await taste.SuggestAsync(
                userId, GrowBy, [.. _queue.Select(t => t.Id)]);

            if (suggestions.Count == 0)
            {
                // Either the profile isn't ready or the library has nothing left to offer. Both
                // mean the music stops, which is what it did before this existed.
                log.LogDebug("Nothing to continue with for {User}", userId);
                return false;
            }

            var added = await LoadTracksAsync([.. suggestions.Select(s => s.TrackId)]);
            if (added.Count == 0) return false;

            var firstNew = _queue.Count;
            _queue.AddRange(added);

            for (var i = firstNew; i < _queue.Count; i++)
            {
                _order.Add(i);
            }

            log.LogInformation("Added {Count} suggested track(s) to keep {User} going", added.Count, userId);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not extend the queue");
            return false;
        }
        finally
        {
            _growing = false;
        }
    }

    /// <summary>How many tracks a top-up adds. Enough to keep going, few enough to stay steerable.</summary>
    private const int GrowBy = 10;

    // ---- listening along -------------------------------------------------

    /// <summary>
    /// Tells <see cref="ListeningService"/> where we are, at most once every
    /// <see cref="ListeningService.HeartbeatInterval"/> unless <paramref name="force"/> says this
    /// is one of the moments that matter — a track change, a pause, a resume.
    ///
    /// The throttle is the point. <see cref="OnTimeUpdate"/> fires several times a second while a
    /// song plays, and a write per tick would mean one listener generating thousands of updates an
    /// hour against the same SQLite file that is serving the website. The service's staleness
    /// window is sized around this interval, not the other way round.
    ///
    /// Failures are swallowed. This is a courtesy to other people looking at a playlist; it is
    /// never a reason for somebody's music to stop.
    /// </summary>
    private async Task BroadcastAsync(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastBroadcast < ListeningService.HeartbeatInterval) return;

        _lastBroadcast = now;

        try
        {
            if (await currentUser.GetIdAsync() is not { } userId) return;

            await listening.PublishAsync(
                userId, SourcePlaylistId, Current?.Id, Position, IsPlaying);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not update the listening-along session");
        }
    }

    /// <summary>Whether this account is sharing what it plays. Read from the stored preference.</summary>
    public async Task<bool> IsSharingListeningAsync()
    {
        if (await currentUser.GetIdAsync() is not { } userId) return false;
        return await listening.IsSharingAsync(userId);
    }

    /// <summary>
    /// Flips the switch and, when it goes on, immediately publishes what is already playing —
    /// otherwise turning it on mid-song does nothing visible for up to a heartbeat, which reads
    /// as a broken button.
    /// </summary>
    public async Task<bool> SetSharingListeningAsync(bool enabled)
    {
        if (await currentUser.GetIdAsync() is not { } userId) return false;

        await listening.SetSharingAsync(userId, enabled);

        if (enabled) await BroadcastAsync(force: true);

        await NotifyAsync();
        return enabled;
    }

    // ---- callbacks from player.js ---------------------------------------

    [JSInvokable]
    public async Task OnTimeUpdate(double position, double duration)
    {
        Position = position;
        if (duration > 0)
        {
            Duration = duration;
        }

        // Remembered while the player still has it: by the time a track change is handled,
        // Position belongs to the next song.
        if (Current is not null && Current.Id == _countingTrackId)
        {
            _countedSeconds = position;
        }

        // Throttled inside; this fires several times a second.
        await BroadcastAsync();

        await NotifyAsync();
    }

    [JSInvokable]
    public async Task OnEnded() => await NextAsync();

    /// <summary>
    /// The browser gave up on the original and took the transcoded copy — either it said so
    /// up front, errored, or stalled without producing metadata.
    /// </summary>
    [JSInvokable]
    public async Task OnFallbackUsed()
    {
        if (UsingFallback) return;

        UsingFallback = true;
        log.LogInformation("Serving {Track} transcoded — the browser couldn't play the original", Current?.Title);
        await NotifyAsync();
    }

    [JSInvokable]
    public async Task OnPlayStateChanged(bool playing)
    {
        IsPlaying = playing;
        await BroadcastAsync(force: true);
        await NotifyAsync();
    }

    [JSInvokable]
    public Task OnError(string message)
    {
        // Most likely a file the scanner listed but the browser can't decode.
        log.LogWarning("Playback error for {Track}: {Message}", Current?.Title, message);
        IsPlaying = false;
        return NotifyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Best effort, and only that: a circuit torn down by a closed laptop never gets here at
        // all, which is why ListeningService expires a session on age rather than on being told.
        // Doing it anyway means the common case — navigating away, signing out — is instant.
        try
        {
            // The last track of a session is only ever recorded here — nothing else follows it.
            await FlushPlayAsync();

            if (await currentUser.GetIdAsync() is { } userId)
            {
                await listening.StopAsync(userId);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not clear the listening-along session on teardown");
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone; nothing to clean up on the other side.
            }
        }

        _selfRef?.Dispose();
    }
}
