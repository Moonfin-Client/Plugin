using Moonfin.LiveTv;
using Xunit;

namespace Moonfin.Bookmarks.Tests;

public sealed class UserBookmarkStoreTests : IDisposable
{
    private static readonly Guid Ann = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // Like the real helpers, an unparseable file reads as null rather than throwing.
    private sealed class TolerantStateFile : IStateFile
    {
        public T? Read<T>(string path, Func<string, T?> parse)
            where T : class
        {
            try
            {
                return File.Exists(path) ? parse(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Write(string path, string contents) => File.WriteAllText(path, contents);
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moonfin-bookmarks-" + Guid.NewGuid().ToString("N"));

    private UserBookmarkStore Store() => new(_folder, new TolerantStateFile());

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task SavingNotesKeepsTheItemsBookmarks()
    {
        var store = Store();
        await store.SaveItemBookmarksAsync(Ann, "42", new List<BookmarkDto> { new() { PositionMs = 1000, Label = "Start" } });
        await store.SaveItemNotesAsync(Ann, "42", new List<NoteDto> { new() { Id = "n1", PositionMs = 2000, Body = "Note" } });

        var data = await Store().GetItemUserDataAsync(Ann, "42");

        Assert.Equal("Start", Assert.Single(data.Bookmarks).Label);
        Assert.Equal("Note", Assert.Single(data.Notes).Body);
    }

    [Fact]
    public async Task UserWithNoFileGetsAnEmptyEnvelope()
    {
        var env = await Store().GetUserBookmarksAsync(Ann);

        Assert.Equal(Ann, env.UserId);
        Assert.Empty(env.Items);
    }

    [Fact]
    public async Task UnreadableFileThrowsInsteadOfReadingAsEmpty()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, $"{Ann}.json"), "{ not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => Store().GetUserBookmarksAsync(Ann));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveItemNotesAsync(Ann, "42", new List<NoteDto>()));
    }
}
