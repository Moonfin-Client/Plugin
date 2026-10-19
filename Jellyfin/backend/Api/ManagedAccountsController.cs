using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Which administrators manage which accounts, for clients that show a user picker with only
/// the family of whoever signed in. Everything here is optional: a client that never calls it
/// keeps working exactly as before.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class ManagedAccountsController : ControllerBase
{
    private readonly IUserDirectory _directory;

    public ManagedAccountsController(IUserDirectory directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// The accounts the caller manages, as they would appear in a user picker. Empty for anyone
    /// who is not an administrator.
    /// </summary>
    [HttpGet("Managed")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IEnumerable<ManagedAccountResponse>> GetManaged()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        var records = MoonfinPlugin.Instance?.Configuration.ManagedAccounts ?? [];
        var managed = ManagedAccounts.ManagedBy(records, _directory.Users(), userId.Value);
        return Ok(managed.Select(u => new ManagedAccountResponse { Id = u.Id.ToString("N"), Name = u.Name, ImageTag = u.ImageTag }));
    }

    /// <summary>
    /// Sets which administrators manage an account, replacing the list. An empty list removes it.
    /// </summary>
    [HttpPut("Users/{userId}/ManagedBy")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ManagedByResponse> SetManagedBy([FromRoute] Guid userId, [FromBody] ManagedByRequest request)
    {
        var plugin = MoonfinPlugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The plugin is not ready" });
        }

        var managers = new List<Guid>();
        foreach (var value in request.ManagedBy ?? [])
        {
            if (!Guid.TryParse(value, out var manager))
            {
                return BadRequest(new { error = "Each manager must be a user id" });
            }

            managers.Add(manager);
        }

        var directory = _directory.Users();
        var problem = ManagedAccounts.Problem(directory, userId, managers.Distinct().ToList());
        if (problem != null)
        {
            return directory.All(u => u.Id != userId) ? NotFound(new { error = problem }) : BadRequest(new { error = problem });
        }

        ManagedAccounts.Set(plugin.Configuration.ManagedAccounts, userId, managers);
        plugin.SaveConfiguration();

        return Ok(new ManagedByResponse
        {
            ManagedBy = ManagedAccounts.ManagersOf(plugin.Configuration.ManagedAccounts, userId).Select(g => g.ToString("N")).ToList()
        });
    }
}

/// <summary>An account in the caller's family.</summary>
public class ManagedAccountResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; set; }
}

public class ManagedByRequest
{
    [JsonPropertyName("managedBy")]
    public List<string>? ManagedBy { get; set; }
}

public class ManagedByResponse
{
    [JsonPropertyName("managedBy")]
    public List<string> ManagedBy { get; set; } = new();
}
