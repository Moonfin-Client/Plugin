using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.MiniGames;

namespace Moonfin.Server.Api;

/// <summary>The top 3 of each Moonfin After Hours game, shared by everyone on the server.</summary>
[ApiController]
[Route("Moonfin/MiniGames")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class MiniGamesController : ControllerBase
{
    private readonly MiniGameScoreService _scores;

    public MiniGamesController(MiniGameScoreService scores)
    {
        _scores = scores;
    }

    /// <summary>The game's top 3, best first.</summary>
    [HttpGet("{gameId}/Scores")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<MiniGameBoardEntry>> GetScores([FromRoute] string gameId)
    {
        if (!MiniGameScoreService.IsGame(gameId))
        {
            return NotFound(new { error = "There is no such game" });
        }

        return Ok(_scores.Top(gameId));
    }

    /// <summary>Records a finished run for the caller and says where it placed.</summary>
    [HttpPost("{gameId}/Scores")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MiniGameRank> PostScore([FromRoute] string gameId, [FromBody] JsonElement body)
    {
        if (!MiniGameScoreService.IsGame(gameId))
        {
            return NotFound(new { error = "There is no such game" });
        }

        // An API key has no user, so it can't hold a place on the board.
        var userId = this.GetUserIdFromClaims();
        var userName = User.Identity?.Name;
        if (userId == null || userId == Guid.Empty || string.IsNullOrEmpty(userName))
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        var score = MiniGameScoreService.ReadScore(body);
        if (score == null)
        {
            return BadRequest(new { error = "The score must be a whole number from 0 to 9,999,999" });
        }

        return Ok(new MiniGameRank { Rank = _scores.Submit(gameId, userId.Value, userName, score.Value) });
    }
}
