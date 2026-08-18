using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Controller for managing user audiobook and book bookmarks &amp; notes sync.
/// </summary>
[ApiController]
[Route("Moonfin/Bookmarks")]
[Produces(MediaTypeNames.Application.Json)]
public class UserBookmarksController : ControllerBase
{
    private readonly UserBookmarksService _bookmarksService;
    private readonly ILogger<UserBookmarksController> _logger;

    public UserBookmarksController(
        UserBookmarksService bookmarksService,
        ILogger<UserBookmarksController> logger)
    {
        _bookmarksService = bookmarksService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all bookmarks and notes for the authenticated user across all items.
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserBookmarksEnvelope>> GetAllBookmarks()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var envelope = await _bookmarksService.GetUserBookmarksAsync(userId.Value);
        return Ok(envelope);
    }

    /// <summary>
    /// Gets bookmarks and notes for a specific item for the authenticated user.
    /// </summary>
    [HttpGet("{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserItemUserDataDto>> GetItemUserData([FromRoute] string itemId)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var data = await _bookmarksService.GetItemUserDataAsync(userId.Value, itemId);
        return Ok(data);
    }

    /// <summary>
    /// Saves bookmarks for a specific item for the authenticated user.
    /// </summary>
    [HttpPost("{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> SaveItemBookmarks(
        [FromRoute] string itemId,
        [FromBody] List<BookmarkDto> bookmarks)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        await _bookmarksService.SaveItemBookmarksAsync(userId.Value, itemId, bookmarks ?? new List<BookmarkDto>());
        return Ok(new { Success = true, ItemId = itemId });
    }

    /// <summary>
    /// Saves notes for a specific item for the authenticated user.
    /// </summary>
    [HttpPost("{itemId}/Notes")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> SaveItemNotes(
        [FromRoute] string itemId,
        [FromBody] List<NoteDto> notes)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        await _bookmarksService.SaveItemNotesAsync(userId.Value, itemId, notes ?? new List<NoteDto>());
        return Ok(new { Success = true, ItemId = itemId });
    }
}
