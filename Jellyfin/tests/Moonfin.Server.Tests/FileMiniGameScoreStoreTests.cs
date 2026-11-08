using Moonfin.MiniGames;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class FileMiniGameScoreStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-minigames-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FileMiniGameScoreStore Store() => new(_dir, new JellyfinStateFile());

    [Fact]
    public void NoFileIsAnEmptyBoard() => Assert.Empty(Store().Load());

    [Fact]
    public void BoardsSurviveARestart()
    {
        var user = Guid.NewGuid();
        new MiniGameScoreService(Store()).Submit("buffer-run", user, "moonfin", 18420);

        var board = new MiniGameScoreService(Store()).Top("buffer-run");

        Assert.Equal("moonfin", Assert.Single(board).UserName);
        Assert.Equal(18420, board[0].Score);
        Assert.Contains("\"userId\":\"" + user.ToString("N") + "\"", File.ReadAllText(Path.Combine(_dir, "scores.json")));
    }
}
