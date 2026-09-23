using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;
using Mootify.Services.Import;
using Mootify.Services.Soulseek;
using Mootify.Services.Transcoding;

namespace Mootify.Services.Requests;

public sealed record ReconcileSummary(
    int Open, int Batches, int Downloading, int Completed, int Searched, int AwaitingSearch, int Failed)
{
    public static readonly ReconcileSummary Idle = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Reconciles Mootify requests with their slskd download batches.</summary>
public sealed class RequestReconciler(
    IDbContextFactory<MootifyDbContext> dbFactory,
    SoulseekClient soulseek,
    LibraryScanner scanner,
    Transcoder transcoder,
    RequestFulfiller fulfiller,
    IOptionsMonitor<SoulseekOptions> soulseekOptions,
    IOptionsMonitor<LibraryOptions> libraryOptions,
    ILogger<RequestReconciler> log)
{
    private const int MaxOfflineRecoveryAttempts = 5;
    private const int MaxRecoverySearchesPerPass = 3;

    public async Task<ReconcileSummary> ReconcileAllAsync(CancellationToken ct = default)
    {
        if (!soulseek.IsConfigured) return ReconcileSummary.Idle;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.Requests
            .Where(r => (r.Status != RequestStatus.Available
                      && r.Status != RequestStatus.NotFound
                      && r.Status != RequestStatus.Failed)
                     || (r.Status == RequestStatus.Failed
                      && r.OfflineRecoveryAttempts == 0
                      && r.FailureReason != null
                      && (EF.Functions.Like(r.FailureReason, "%user not online%")
                          || EF.Functions.Like(r.FailureReason, "%user is offline%"))))
            .ToListAsync(ct);
        if (open.Count == 0) return ReconcileSummary.Idle;

        var downloading = 0;
        var completed = 0;
        var failed = 0;
        var recoverySearches = 0;

        foreach (var request in open)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Status == RequestStatus.Failed && SoulseekClient.IsPeerOffline(request.FailureReason))
            {
                request.Status = RequestStatus.Searching;
                request.CompletedAt = null;
                request.NextOfflineRecoveryAt = DateTimeOffset.UtcNow;
            }
            if (request.NextOfflineRecoveryAt is { } due)
            {
                if (due <= DateTimeOffset.UtcNow && recoverySearches < MaxRecoverySearchesPerPass)
                {
                    recoverySearches++;
                    await RecoverOfflinePeerAsync(request, ct);
                }
                if (request.Status == RequestStatus.Failed) failed++;
                else downloading++;
                continue;
            }
            if (request.SoulseekBatchId is not { } batchId)
            {
                Fail(request, "This request predates the Soulseek integration. Please request it again.");
                failed++;
                continue;
            }

            try
            {
                var batch = await soulseek.GetBatchAsync(batchId, ct);
                if (batch is null || batch.Transfers.Count == 0) { downloading++; continue; }

                var terminal = batch.Transfers.All(t => Has(t.State, "Completed"));
                if (!terminal)
                {
                    request.Status = RequestStatus.Downloading;
                    request.UpdatedAt = DateTimeOffset.UtcNow;
                    downloading++;
                    continue;
                }

                var unsuccessful = batch.Transfers.FirstOrDefault(t => !Has(t.State, "Succeeded"));
                if (unsuccessful is not null)
                {
                    var reason = unsuccessful.Exception ?? $"Soulseek download ended as {unsuccessful.State}.";
                    if (SoulseekClient.IsPeerOffline(reason))
                    {
                        request.FailureReason = reason;
                        request.Status = RequestStatus.Searching;
                        request.NextOfflineRecoveryAt = DateTimeOffset.UtcNow;
                        if (recoverySearches < MaxRecoverySearchesPerPass)
                        {
                            recoverySearches++;
                            await RecoverOfflinePeerAsync(request, ct);
                        }
                        if (request.Status == RequestStatus.Failed) failed++;
                        else downloading++;
                    }
                    else
                    {
                        Fail(request, reason);
                        failed++;
                    }
                    continue;
                }

                if (await CompleteAsync(db, request, ct)) completed++;
                else downloading++;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Reconciling Soulseek batch {BatchId} failed", batchId);
            }
        }

        await db.SaveChangesAsync(ct);
        return new ReconcileSummary(open.Count, open.Count, downloading, completed, 0, 0, failed);
    }

    private async Task RecoverOfflinePeerAsync(Request request, CancellationToken ct)
    {
        if (request.OfflineRecoveryAttempts >= MaxOfflineRecoveryAttempts)
        {
            Fail(request, "The Soulseek user went offline, and no matching download was available after several searches.");
            request.NextOfflineRecoveryAt = null;
            return;
        }

        request.OfflineRecoveryAttempts++;
        request.Status = RequestStatus.Searching;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        request.NextOfflineRecoveryAt = request.UpdatedAt.Add(RecoveryDelay(request.OfflineRecoveryAttempts));

        try
        {
            var results = await soulseek.SearchAsync(request.Query, ct);
            var originalTitle = NormalizedFileTitle(request.SoulseekFilename ?? "");
            var query = PlaylistImportService.Normalize(request.Query);
            var artist = query.EndsWith(" " + originalTitle, StringComparison.Ordinal)
                ? query[..^(originalTitle.Length + 1)]
                : "";
            var matches = results.Where(file =>
                    originalTitle.Length > 0
                    && NormalizedFileTitle(file.Filename) == originalTitle
                    && (artist.Length == 0 || PlaylistImportService.Normalize(file.Folder).Contains(artist,
                        StringComparison.Ordinal)))
                .OrderBy(file => string.Equals(file.Username, request.SoulseekUsername,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in matches)
            {
                // A fresh batch id avoids colliding with slskd's terminal batch. Keep the
                // destination tied to the request so completion still finds the downloaded file.
                var newBatchId = Guid.NewGuid();
                var (ok, error) = await soulseek.EnqueueAsync(newBatchId, file, ct, request.Id);
                if (!ok)
                {
                    log.LogInformation("Could not retry request {RequestId} from {Peer}: {Error}",
                        request.Id, file.Username, error);
                    continue;
                }

                var oldBatchId = request.SoulseekBatchId;
                request.SoulseekBatchId = newBatchId;
                request.SoulseekUsername = file.Username;
                request.SoulseekFilename = file.Filename;
                request.Status = RequestStatus.Downloading;
                request.FailureReason = null;
                request.NextOfflineRecoveryAt = null;
                request.UpdatedAt = DateTimeOffset.UtcNow;
                if (oldBatchId is { } old && old != newBatchId)
                    await soulseek.CancelBatchAsync(old, ct);
                log.LogInformation("Retried request {RequestId} from Soulseek user {Peer}", request.Id, file.Username);
                return;
            }

            log.LogInformation("No matching online Soulseek peer found for request {RequestId}; retry {Attempt}/{MaxAttempts} is due at {Due}",
                request.Id, request.OfflineRecoveryAttempts, MaxOfflineRecoveryAttempts, request.NextOfflineRecoveryAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Searching for another Soulseek peer for request {RequestId} failed", request.Id);
        }
    }

    private static TimeSpan RecoveryDelay(int attempts) => attempts switch
    {
        1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(30),
        3 => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(6),
    };

    private static string NormalizedFileTitle(string filename)
    {
        var title = Path.GetFileNameWithoutExtension(filename.Replace('\\', '/'));
        title = System.Text.RegularExpressions.Regex.Replace(title, @"^\s*\d{1,3}\s*[-._ ]+\s*", "");
        return PlaylistImportService.Normalize(title);
    }

    private async Task<bool> CompleteAsync(MootifyDbContext db, Request request, CancellationToken ct)
    {
        var localRoot = string.IsNullOrWhiteSpace(soulseekOptions.CurrentValue.LocalDownloadRoot)
            ? libraryOptions.CurrentValue.MusicRoot
            : soulseekOptions.CurrentValue.LocalDownloadRoot;
        var folder = Path.GetFullPath(Path.Combine(localRoot, soulseek.DestinationFor(request.Id).Replace('/', Path.DirectorySeparatorChar)));

        if (!Directory.Exists(folder))
        {
            log.LogWarning("Soulseek batch {BatchId} completed but {Folder} is not visible to Mootify", request.SoulseekBatchId, folder);
            return false;
        }

        if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(Transcoder.NeedsTranscode))
        {
            if (!transcoder.IsAvailable)
            {
                Fail(request, "The download needs conversion, but FFmpeg is unavailable.");
                return false;
            }
            request.Status = RequestStatus.Transcoding;
            request.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await transcoder.TranscodeFolderAsync(folder, ct);
        }

        await scanner.ScanPathAsync(folder, ct);

        await using var lookup = await dbFactory.CreateDbContextAsync(ct);
        var trackIds = await lookup.Tracks.AsNoTracking()
            .Where(t => t.IsPresent && t.Path.StartsWith(folder))
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (trackIds.Count == 0)
        {
            log.LogWarning("Soulseek batch {BatchId} completed but no audio was indexed under {Folder}", request.SoulseekBatchId, folder);
            return false;
        }

        await fulfiller.CompleteAsync(db, request, request.Kind == RequestKind.Track ? [trackIds[0]] : trackIds, ct);
        return true;
    }

    private static bool Has(string state, string flag) =>
        state.Split(',', StringSplitOptions.TrimEntries).Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static void Fail(Request request, string reason)
    {
        request.Status = RequestStatus.Failed;
        request.FailureReason = reason;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        request.CompletedAt = request.UpdatedAt;
    }
}

public sealed class RequestReconcilerService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<SoulseekOptions> options,
    ILogger<RequestReconcilerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.IsConfigured)
        {
            log.LogInformation("Soulseek is not configured; the request reconciler is idle.");
            return;
        }

        using var timer = new PeriodicTimer(options.CurrentValue.PollInterval);
        try
        {
            do
            {
                using var scope = scopeFactory.CreateScope();
                try { await scope.ServiceProvider.GetRequiredService<RequestReconciler>().ReconcileAllAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Reconciliation pass failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
