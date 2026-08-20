using Microsoft.AspNetCore.Builder;
using Mootify.Services.Auth;
using Mootify.Services.Library;

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
    /// Forwarded to <see cref="LibraryMatch.Clean"/>, which is also where the predicates live: the
    /// website and the API used to disagree about what a search term matches, and one of the two
    /// answers was always wrong.
    /// </summary>
    public static string? Clean(string? query) => LibraryMatch.Clean(query);

    /// <inheritdoc cref="LibraryMatch.LikePattern"/>
    public static string LikePattern(string cleaned) => LibraryMatch.LikePattern(cleaned);

    public static bool HasQuery(string? query) => Clean(query) is not null;
}
