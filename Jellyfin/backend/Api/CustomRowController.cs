using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

[ApiController]
[Route("Moonfin/CustomRows")]
public class CustomRowController : ControllerBase
{
    private readonly CustomRowCacheService _cacheService;
    private readonly CustomRowFetchService _fetchService;
    private readonly ILogger<CustomRowController> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public CustomRowController(
        CustomRowCacheService cacheService,
        CustomRowFetchService fetchService,
        ILogger<CustomRowController> logger)
    {
        _cacheService = cacheService;
        _fetchService = fetchService;
        _logger = logger;
    }

    [HttpGet("Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CustomRowResponse>> GetCustomRowItems(
        [FromQuery] string source,
        [FromQuery] string type,
        [FromQuery] string @params,
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(@params))
        {
            return BadRequest(new { Error = "Missing required parameters: source, type, params" });
        }

        source = source.Trim().ToLowerInvariant();
        type = type.Trim().ToLowerInvariant();

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        Dictionary<string, string> parsedParams;
        try
        {
            parsedParams = JsonSerializer.Deserialize<Dictionary<string, string>>(@params) ?? new();
        }
        catch (JsonException)
        {
            return BadRequest(new { Error = "params must be a JSON object of string values" });
        }

        // Older clients force a refresh by putting a "_nocache" timestamp in params.
        // Treat that as refresh=true and strip underscore-prefixed keys so the same
        // list always maps to the same cache entry.
        if (parsedParams.Keys.Any(k => k.StartsWith('_')))
        {
            refresh = true;
            parsedParams = parsedParams.Where(kv => !kv.Key.StartsWith('_'))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        var cacheKey = CustomRowFetchService.ComputeCacheKey(source, type, parsedParams);

        if (!refresh)
        {
            var cachedItems = _cacheService.TryGet(cacheKey, CacheTtl);
            if (cachedItems != null)
            {
                return Ok(new CustomRowResponse
                {
                    Success = true,
                    Items = cachedItems
                });
            }
        }

        try
        {
            var items = await _fetchService.FetchCustomRowAsync(source, type, parsedParams, userId.Value, cancellationToken);

            // Don't cache empty results. An empty row cached for 24h looks like a
            // broken list when the real cause was an upstream hiccup.
            if (items.Count > 0)
            {
                _cacheService.Set(cacheKey, items);
                _cacheService.PruneOlderThan(TimeSpan.FromDays(7));
                await _cacheService.FlushAsync();
            }

            return Ok(new CustomRowResponse
            {
                Success = true,
                Items = items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve custom row for source: {Source}, type: {Type}", source, type);
            return Ok(new CustomRowResponse
            {
                Success = false,
                Error = ex.Message
            });
        }
    }
}

public class CustomRowResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();
}
