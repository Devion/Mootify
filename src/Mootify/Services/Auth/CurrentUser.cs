using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Mootify.Services.Auth;

/// <summary>
/// Resolves the signed-in user for a circuit. Everything that needs "who is this" goes
/// through here rather than digging claims out by hand in components.
/// </summary>
public sealed class CurrentUser(AuthenticationStateProvider authState)
{
    private Guid? _cachedId;
    private string? _cachedName;
    private bool _cachedIsAdmin;

    public async Task<Guid?> GetIdAsync()
    {
        if (_cachedId is not null) return _cachedId;

        var principal = (await authState.GetAuthenticationStateAsync()).User;
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(raw, out var id))
        {
            _cachedId = id;
            _cachedName = principal.FindFirstValue(ClaimTypes.Name);
            _cachedIsAdmin = principal.IsInRole(MootifyAuth.AdminRole);
            return id;
        }

        return null;
    }

    /// <summary>Drives whether the Admin link is shown. Never the authorization decision
    /// itself — every admin operation re-checks against the database.</summary>
    public async Task<bool> IsAdminAsync()
    {
        await GetIdAsync();
        return _cachedIsAdmin;
    }

    public async Task<Guid> GetRequiredIdAsync() =>
        await GetIdAsync() ?? throw new InvalidOperationException("No signed-in user on this circuit.");

    public async Task<string> GetDisplayNameAsync()
    {
        if (_cachedName is not null) return _cachedName;
        await GetIdAsync();
        return _cachedName ?? "there";
    }
}
