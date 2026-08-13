using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Mootify.Data;

public sealed class MootifyDbContext(DbContextOptions<MootifyDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<UserPreference> Preferences => Set<UserPreference>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamMembershipRequest> TeamMembershipRequests => Set<TeamMembershipRequest>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PlaybackState> PlaybackStates => Set<PlaybackState>();
    public DbSet<PlayEvent> PlayEvents => Set<PlayEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no DateTimeOffset type — it falls back to TEXT, and "ORDER BY a text
        // column" either throws or sorts lexicographically, which is wrong the moment an
        // offset differs. The binary converter packs the value into a sortable long.
        // Applied as a convention so no query has to remember this.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasOne(x => x.Preference)
             .WithOne(x => x.User!)
             .HasForeignKey<UserPreference>(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserPreference>().HasKey(x => x.UserId);

        b.Entity<AppSetting>().HasKey(x => x.Key);

        b.Entity<ApiToken>(e =>
        {
            // Every authenticated API request is a lookup by hash, so it has to be an index
            // rather than a scan. Unique because two rows for one secret is nonsense.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            // Deleting an account takes its devices with it, the same as its playlists.
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Artist>(e =>
        {
            e.HasIndex(x => x.SortName);
            e.HasIndex(x => x.MusicBrainzId);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Album>(e =>
        {
            e.HasIndex(x => x.MusicBrainzId);
            e.HasIndex(x => new { x.ArtistId, x.Title }).IsUnique();
            e.HasOne(x => x.Artist)
             .WithMany(x => x.Albums)
             .HasForeignKey(x => x.ArtistId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Track>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
            e.HasIndex(x => x.RecordingMusicBrainzId);
            e.HasIndex(x => x.Title);
            e.HasOne(x => x.Artist)
             .WithMany(x => x.Tracks)
             .HasForeignKey(x => x.ArtistId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Album)
             .WithMany(x => x.Tracks)
             .HasForeignKey(x => x.AlbumId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Team>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<TeamMember>(e =>
        {
            e.HasKey(x => new { x.TeamId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Team)
             .WithMany(x => x.Members)
             .HasForeignKey(x => x.TeamId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TeamMembershipRequest>(e =>
        {
            // One pending row per person per team, whichever direction it came from. Stops an
            // owner spamming invites, and stops an invite and an application racing each other.
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Team)
             .WithMany(x => x.PendingRequests)
             .HasForeignKey(x => x.TeamId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Playlist>(e =>
        {
            e.HasIndex(x => x.OwnerUserId);
            e.HasIndex(x => x.TeamId);
            e.HasOne(x => x.Owner)
             .WithMany()
             .HasForeignKey(x => x.OwnerUserId)
             .OnDelete(DeleteBehavior.Cascade);
            // Deleting a team takes its playlists with it — they belong to the team, not to
            // whoever happened to create them.
            e.HasOne(x => x.Team)
             .WithMany(x => x.Playlists)
             .HasForeignKey(x => x.TeamId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlaylistItem>(e =>
        {
            e.HasIndex(x => new { x.PlaylistId, x.SortKey });
            // Makes the auto-append idempotent: the webhook and the poller will both fire eventually.
            e.HasIndex(x => new { x.PlaylistId, x.TrackId, x.RequestId })
             .IsUnique()
             .HasFilter(null);
            e.Property(x => x.SortKey).IsConcurrencyToken();
            e.HasOne(x => x.Playlist)
             .WithMany(x => x.Items)
             .HasForeignKey(x => x.PlaylistId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track)
             .WithMany()
             .HasForeignKey(x => x.TrackId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Request>(e =>
        {
            e.HasIndex(x => new { x.RequesterId, x.Status });
            e.HasIndex(x => x.LidarrAlbumId);
            e.HasOne(x => x.Requester)
             .WithMany()
             .HasForeignKey(x => x.RequesterId)
             .OnDelete(DeleteBehavior.Cascade);
            // Deleting a playlist must not delete the request that was headed for it.
            e.HasOne(x => x.TargetPlaylist)
             .WithMany()
             .HasForeignKey(x => x.TargetPlaylistId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlaybackState>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlayEvent>(e => e.HasIndex(x => new { x.UserId, x.PlayedAt }));
    }
}
