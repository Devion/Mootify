using Microsoft.AspNetCore.Builder;
using Mootify.Services.Auth;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Shared plumbing for the JSON API: the authenticated route group, and the two bits of
/// query handling every list endpoint repeats.
/// </summary>
public static class ApiSetup
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        app.MapApiAuthEndpoints();
        app.MapApiLibraryEndpoints();
        app.MapApiPlaylistEndpoints();
        app.MapApiRequestEndpoints();
        app.MapApiSyncEndpoints();
        app.MapArtEndpoints();
    }

    /// <summary>
    /// Token-authenticated, antiforgery off. Both halves of that are deliberate: the policy
    /// excludes the cookie scheme so a browser session can never be ridden into a state-changing
    /// API call, and with cookies excluded there is no CSRF left for antiforgery to prevent.
    /// </summary>
    public static RouteGroupBuilder MapApiGroup(this IEndpointRouteBuilder app, string prefix) =>
        app.MapGroup(prefix)
            .RequireAuthorization(MootifyAuth.ApiPolicy)
            .DisableAntiforgery();

    /// <summary>
    /// Clamps paging. <c>take=0</c> from a client that forgot to set it would otherwise mean
    /// "nothing", and an unbounded take means a head unit trying to hold 40,000 rows.
    /// </summary>
    public static (int Skip, int Take) Page(int? skip, int? take, int maxPageSize, int defaultTake = 100)
    {
        var s = Math.Max(0, skip ?? 0);
        var t = take is null or <= 0 ? defaultTake : take.Value;
        return (s, Math.Min(t, maxPageSize));
    }

    /// <summary>
    /// A search box's contents, or null if there's nothing in it worth searching for.
    ///
    /// Wildcards are dropped rather than escaped — no song title has a bare <c>%</c> somebody is
    /// looking for, and escaping needs a <c>LIKE … ESCAPE</c> the provider may not translate. That
    /// leaves the case where the term was <i>only</i> wildcards, which is why this returns null
    /// rather than a string: <c>q=%</c> has to mean "you typed nothing useful", not "match the
    /// entire library".
    /// </summary>
    public static string? Clean(string? query)
    {
        if (query is null) return null;

        var cleaned = query.Replace("%", "").Replace("_", "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Wraps a cleaned term for LIKE. SQLite's LIKE is case-insensitive for ASCII, which
    /// <c>string.Contains</c> is not — EF translates that one to <c>instr()</c>, and a
    /// case-sensitive library search finds nothing anybody typed.
    /// </summary>
    public static string LikePattern(string cleaned) => $"%{cleaned}%";

    public static bool HasQuery(string? query) => Clean(query) is not null;
}
