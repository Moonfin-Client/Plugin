using System.Text;
using Emby.Plugins.Moonfin.Services;
using Moonfin.Games;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The shared games code runs on Emby through stand-ins for APIs netstandard2.1 lacks. These pin
/// that the stand-ins answer the way the framework's own versions do.
/// </summary>
public class SharedCodeShimsTests
{
    [Fact]
    public void LogTemplatesFillTheirHolesInOrder()
    {
        var text = EmbyLogger<SharedCodeShimsTests>.Render(
            "Refusing {OriginalPath}: {Width}x{Height} over {Seconds:0.0}s {{literal}}",
            new object?[] { "/roms/a.png", 4000, 3000, 2.5 });

        Assert.Equal("Refusing /roms/a.png: 4000x3000 over 2.5s {literal}", text);
        Assert.Equal("missing (null)", EmbyLogger<SharedCodeShimsTests>.Render("missing {Value}", new object?[0]));
    }

    [Fact]
    public void HashesComeOutAsLowercaseHex()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            GamesCompat.LowerHex(GamesCompat.Sha256(Encoding.ASCII.GetBytes("abc"))));
        Assert.Equal(
            "a9993e364706816aba3e25717850c26c9cd0d89d",
            GamesCompat.LowerHex(GamesCompat.Sha1(Encoding.ASCII.GetBytes("abc"))));
    }

    [Fact]
    public async Task WaitAsyncStopsOnCancellationAndOtherwiseReturnsTheResult()
    {
        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource<int>().Task;
        var waiting = never.WaitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(7, await Task.FromResult(7).WaitAsync(CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() => Task.Delay(Timeout.Infinite).WaitAsync(TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void MoveOverwriteReplacesTheTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moonfin-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target.json");
            var temp = target + ".tmp";
            File.WriteAllText(target, "old");
            File.WriteAllText(temp, "new");

            GamesCompat.MoveOverwrite(temp, target);

            Assert.Equal("new", File.ReadAllText(target));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
