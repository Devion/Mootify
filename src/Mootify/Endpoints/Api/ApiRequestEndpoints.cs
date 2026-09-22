using Microsoft.Extensions.Caching.Memory;
using Mootify.Services.Auth;
using Mootify.Services.Requests;
using Mootify.Services.Soulseek;

namespace Mootify.Endpoints.Api;

public static class ApiRequestEndpoints
{
    private static readonly TimeSpan LookupCacheDuration = TimeSpan.FromMinutes(20);

    public static void MapApiRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var requests = app.MapApiGroup("/api/v1/requests");

        requests.MapGet("", async (HttpContext http, RequestService service, RequestFilter? filter,
            int? skip, int? take, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var (s, t) = ApiSetup.Page(skip, take, RequestService.MaxPageSize, RequestService.DefaultPageSize);
            var page = await service.GetForUserAsync(userId, filter ?? RequestFilter.All, s, t, ct);
            return Results.Ok(new ApiPage<ApiRequest>(page.Total, page.Skip, page.Take, [.. page.Rows.Select(Map)]));
        });

        requests.MapDelete("/{id:guid}", async (HttpContext http, Guid id, RequestService service, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var (ok, error) = await service.CancelAsync(userId, id, asAdmin: false, ct);
            return ok ? Results.NoContent() : Results.Problem(error, statusCode: 404);
        });

        requests.MapGet("/search", async (string? q, SoulseekClient soulseek, IMemoryCache cache, CancellationToken ct) =>
        {
            if (!soulseek.IsConfigured)
                return Results.Problem("Soulseek isn't configured, so nothing new can be fetched.", statusCode: 503);
            if (!ApiSetup.HasQuery(q)) return Results.Ok(new List<ApiSoulseekFile>());

            var files = await soulseek.SearchAsync(q!.Trim(), ct);
            var result = new List<ApiSoulseekFile>(files.Count);
            foreach (var file in files)
            {
                var id = Guid.NewGuid();
                cache.Set(CacheKey(id), file, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = LookupCacheDuration,
                    Size = 1,
                });
                result.Add(new ApiSoulseekFile(id, file.DisplayName, file.Folder, file.Extension,
                    file.Size, file.BitRate, file.BitDepth, file.Length, file.HasFreeUploadSlot, file.QueueLength));
            }
            return Results.Ok(result);
        });

        requests.MapPost("", async (HttpContext http, CreateRequestBody body, RequestService service,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!cache.TryGetValue(CacheKey(body.ResultId), out SoulseekFile? file) || file is null)
                return Results.Problem("That Soulseek result expired. Search again and choose a result.", statusCode: 409);

            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var result = await service.CreateAsync(userId, file, body.SearchTerm ?? file.DisplayName,
                body.TargetPlaylistId, ct);
            return result.Ok
                ? Results.Created($"/api/v1/requests/{result.RequestId}", new { id = result.RequestId })
                : Results.Problem(result.Error, statusCode: 409);
        });
    }

    private static ApiRequest Map(Data.Request r) => new(r.Id, r.Kind, r.Status, r.Query,
        r.ArtistName, r.AlbumTitle, r.TrackTitle, r.TargetPlaylistId, r.TargetPlaylist?.Name,
        r.FailureReason, r.CreatedAt, r.CompletedAt);

    private static string CacheKey(Guid id) => $"api:soulseek:result:{id:N}";
}
