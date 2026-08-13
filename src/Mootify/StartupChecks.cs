using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;
using Mootify.Services.Lidarr;
using Mootify.Services.Settings;
using Mootify.Services.Transcoding;

namespace Mootify;

/// <summary>
/// Everything that should fail loudly at boot rather than quietly at 2am.
/// </summary>
public static class StartupChecks
{
    public static async Task RunAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mootify.Startup");

        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // ---- database -------------------------------------------------------
        var dbFactory = services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        bool anyUsers;

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();

            // EnsureCreated only ever builds an empty file. Tables added after an install exists
            // have to be created by hand until there are migrations — see SchemaPatch.
            await SchemaPatch.ApplyAsync(db, log);

            anyUsers = await db.Users.AnyAsync();
        }

        // ---- first run ------------------------------------------------------
        var setupState = app.Services.GetRequiredService<SetupState>();
        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;

        if (anyUsers)
        {
            setupState.MarkComplete();
        }
        else
        {
            setupState.MarkIncomplete();
            log.LogWarning(
                "===========================================================\n" +
                "  No accounts yet. Open Mootify and create the admin\n" +
                "  account '{AdminUsername}' — everything else is blocked\n" +
                "  until you do.\n" +
                "===========================================================",
                auth.AdminUsername);
        }

        // ---- music root -----------------------------------------------------
        var library = services.GetRequiredService<IOptions<LibraryOptions>>().Value;

        // Opens the SMB session first if the share needs credentials, so the reachability
        // check below reports the truth rather than "not found".
        var shares = services.GetRequiredService<NetworkShareConnector>();
        await shares.EnsureConnectedAsync();

        if (string.IsNullOrWhiteSpace(library.MusicRoot))
        {
            log.LogWarning("Library:MusicRoot is not set — Mootify will start, but the library stays empty.");
        }
        else if (!Directory.Exists(library.MusicRoot))
        {
            log.LogWarning(
                "Library:MusicRoot ({Root}) is not reachable. Remember Lidarr's root folder is a path " +
                "inside its own container; this one has to be the path Mootify can see. If it's a share " +
                "that needs a login, set Library:Username and Library:Password.",
                library.MusicRoot);
        }
        else
        {
            log.LogInformation("Music root: {Root}", library.MusicRoot);
        }

        // ---- ffmpeg ---------------------------------------------------------
        // A hard dependency, not an optional extra: without it, a non-MP3 import can't be
        // rescued and the request fails at the last step.
        var transcoder = services.GetRequiredService<Transcoder>();
        var (ffmpegOk, ffmpegVersion) = await transcoder.ProbeAsync();

        if (ffmpegOk)
        {
            log.LogInformation("FFmpeg: {Version}", ffmpegVersion);
        }
        else
        {
            log.LogWarning(
                "FFmpeg was not found. Mootify runs, but anything Lidarr grabs that isn't an MP3 " +
                "will fail its request instead of being converted.");
        }

        // ---- lidarr ---------------------------------------------------------
        var lidarr = services.GetRequiredService<LidarrClient>();
        var lidarrOptions = services.GetRequiredService<IOptions<LidarrOptions>>().Value;

        if (!lidarrOptions.IsConfigured)
        {
            log.LogInformation("Lidarr is not configured; requests are disabled.");
        }
        else
        {
            var (ok, version, error) = await lidarr.CheckAsync();
            if (ok)
            {
                log.LogInformation("Lidarr {Version} at {Url}", version, lidarrOptions.BaseUrl);
            }
            else
            {
                // Don't throw: a Lidarr that's down shouldn't stop people playing the music
                // they already have.
                log.LogError("Lidarr check failed: {Error}", error);
            }
        }
    }
}
