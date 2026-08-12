using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Library;

public sealed record ShareConnection(bool Connected, string? Detail);

/// <summary>
/// Opens an authenticated SMB session to the music share.
///
/// Windows keeps SMB sessions per logon session, not per handle, so one call here makes every
/// later <c>File.OpenRead</c> and directory walk on that path work unchanged — the scanner and
/// the streaming endpoint need to know nothing about it.
///
/// Windows-only by nature. On Linux the share is mounted by the OS (fstab, or a Docker volume
/// with credentials), which is the right place for it; there we say so and stay out of the way.
/// </summary>
public sealed class NetworkShareConnector(
    IOptionsMonitor<LibraryOptions> options,
    ILogger<NetworkShareConnector> log)
{
    private const int NoError = 0;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorBadNetName = 67;
    private const int ErrorBadNetPath = 53;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorLogonFailure = 1326;
    private const int ResourceTypeDisk = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public ShareConnection? Last { get; private set; }

    /// <summary>
    /// Connects if the path is a UNC share with credentials configured and isn't reachable
    /// already. Safe to call repeatedly — it's the reconnect path when a share drops.
    /// </summary>
    public async Task<ShareConnection> EnsureConnectedAsync(CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var root = opts.MusicRoot;

        if (string.IsNullOrWhiteSpace(root))
        {
            return Record(false, "Library:MusicRoot is not set.");
        }

        // Already readable — either it's local, or the session is up, or the account running
        // Mootify has access on its own.
        if (Directory.Exists(root))
        {
            return Record(true, opts.HasCredentials ? "Share is reachable." : "Reachable without credentials.");
        }

        if (!IsUncPath(root))
        {
            return Record(false, $"{root} does not exist.");
        }

        if (!opts.HasCredentials)
        {
            return Record(false, "Share is not reachable and no Library:Username is configured.");
        }

        if (!OperatingSystem.IsWindows())
        {
            // Saying nothing here would leave somebody staring at an empty library wondering
            // why the credentials they set are being ignored.
            return Record(false,
                "Share credentials only work on Windows. On Linux, mount the share in the OS " +
                "(fstab or a Docker volume) and point Library:MusicRoot at the mount.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have connected while we queued.
            if (Directory.Exists(root))
            {
                return Record(true, "Share is reachable.");
            }

            return Connect(root, opts);
        }
        finally
        {
            _gate.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private ShareConnection Connect(string root, LibraryOptions opts)
    {
        var share = ShareRoot(root);
        var user = string.IsNullOrWhiteSpace(opts.Domain)
            ? opts.Username
            : $"{opts.Domain}\\{opts.Username}";

        var result = AddConnection(share, user, opts.Password);

        // Windows refuses a second session to the same server under different credentials.
        // Drop the old one and try again rather than leaving the library empty.
        if (result == ErrorSessionCredentialConflict)
        {
            log.LogInformation("Existing session to {Share} conflicts; replacing it", share);
            WNetCancelConnection2(share, 0, true);
            result = AddConnection(share, user, opts.Password);
        }

        if (result is NoError or ErrorAlreadyAssigned)
        {
            var reachable = Directory.Exists(root);
            return Record(reachable, reachable
                ? $"Connected to {share} as {user}."
                : $"Connected to {share}, but {root} still isn't there — check the path below the share.");
        }

        return Record(false, $"Could not connect to {share}: {Describe(result)}");
    }

    [SupportedOSPlatform("windows")]
    private static int AddConnection(string share, string? user, string? password)
    {
        var resource = new NetResource
        {
            Scope = 0,
            Type = ResourceTypeDisk,
            DisplayType = 0,
            Usage = 0,
            LocalName = null,          // no drive letter; the UNC path stays as written
            RemoteName = share,
            Comment = null,
            Provider = null,
        };

        return WNetAddConnection2(ref resource, password, user, 0);
    }

    private ShareConnection Record(bool connected, string? detail)
    {
        var previous = Last;
        Last = new ShareConnection(connected, detail);

        // Only log on change — this runs before every scan and would otherwise be noise.
        if (previous is null || previous.Connected != connected || previous.Detail != detail)
        {
            if (connected)
            {
                log.LogInformation("Music share: {Detail}", detail);
            }
            else
            {
                log.LogWarning("Music share unavailable: {Detail}", detail);
            }
        }

        return Last;
    }

    public static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// <c>\\server\share\music\stuff</c> becomes <c>\\server\share</c>. The session is per
    /// share, so connecting to the deepest folder would be both wrong and fragile.
    /// </summary>
    public static string ShareRoot(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var parts = normalized
            .TrimStart('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2
            ? $@"\\{parts[0]}\{parts[1]}"
            : normalized;
    }

    private static string Describe(int code) => code switch
    {
        ErrorLogonFailure or ErrorInvalidPassword => "the username or password was rejected.",
        ErrorBadNetName => "the share name doesn't exist on that host.",
        ErrorBadNetPath => "the host couldn't be reached.",
        _ => $"Windows error {code}.",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Provider;
    }

    // DllImport rather than LibraryImport: NETRESOURCE carries strings, so it isn't blittable
    // and the source generator can't marshal it.
    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
}
