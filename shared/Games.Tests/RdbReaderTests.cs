using Moonfin.Games;
using Xunit;

namespace Moonfin.Games.Tests;

/// <summary>
/// RdbReader parses libretro .rdb databases with a hand-written MessagePack decoder, so anything
/// it mishandles becomes wrong game metadata rather than a crash.
/// </summary>
public class RdbReaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"moonfin-emby-rdb-{Guid.NewGuid():N}");

    private string NewFile(string name)
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    private string WriteDb(IEnumerable<byte[]> records, ulong? metadataOffset = null) =>
        RdbFixtures.Write(NewFile($"{Guid.NewGuid():N}.rdb"), records, metadataOffset);

    [Fact]
    public void ReadAll_ParsesEveryStringFieldOfARecord()
    {
        var record = new MsgPack()
            .FixMap(7)
            .Str("name").Str("Super Game")
            .Str("rom_name").Str("supergame.zip")
            .Str("genre").Str("Platformer")
            .Str("developer").Str("Some Studio")
            .Str("publisher").Str("Some Publisher")
            .Str("franchise").Str("Super")
            .Str("region").Str("USA")
            .ToArray();

        var read = Assert.Single(RdbReader.ReadAll(WriteDb(new[] { record })));

        Assert.Equal("Super Game", read.Name);
        Assert.Equal("supergame.zip", read.RomName);
        Assert.Equal("Platformer", read.Genre);
        Assert.Equal("Some Studio", read.Developer);
        Assert.Equal("Some Publisher", read.Publisher);
        Assert.Equal("Super", read.Franchise);
        Assert.Equal("USA", read.Region);
    }

    [Fact]
    public void ReadAll_ReadsACrcAsABigEndianUnsignedInt()
    {
        var read = Assert.Single(RdbReader.ReadAll(WriteDb(new[] { RdbFixtures.Game("G", crc: 0xDEADBEEF) })));

        Assert.Equal(0xDEADBEEFu, read.Crc);
    }

    [Fact]
    public void ReadAll_IgnoresACrcThatIsNotFourBytes()
    {
        var record = new MsgPack()
            .FixMap(2)
            .Str("name").Str("G")
            .Str("crc").Bin(0x01, 0x02)
            .ToArray();

        Assert.Null(Assert.Single(RdbReader.ReadAll(WriteDb(new[] { record }))).Crc);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(200)]
    [InlineData(1998)]
    [InlineData(65535)]
    public void ReadAll_ReadsAYearAcrossEveryIntegerEncoding(int year)
    {
        var read = Assert.Single(RdbReader.ReadAll(WriteDb(new[] { RdbFixtures.Game("G", year: year) })));

        Assert.Equal(year, read.ReleaseYear);
    }

    [Fact]
    public void ReadAll_TreatsANonNumericYearAsAbsent()
    {
        var record = new MsgPack()
            .FixMap(2)
            .Str("name").Str("G")
            .Str("releaseyear").Str("1998")
            .ToArray();

        Assert.Null(Assert.Single(RdbReader.ReadAll(WriteDb(new[] { record }))).ReleaseYear);
    }

    [Fact]
    public void ReadAll_TreatsAnEmptyStringAsAbsent()
    {
        var record = new MsgPack()
            .FixMap(2)
            .Str("name").Str("G")
            .Str("genre").Str(string.Empty)
            .ToArray();

        Assert.Null(Assert.Single(RdbReader.ReadAll(WriteDb(new[] { record }))).Genre);
    }

    [Fact]
    public void ReadAll_ReadsEveryRecordInTheFile()
    {
        var db = WriteDb(new[]
        {
            RdbFixtures.Game("One"),
            RdbFixtures.Game("Two"),
            RdbFixtures.Game("Three"),
        });

        Assert.Equal(new[] { "One", "Two", "Three" }, RdbReader.ReadAll(db).Select(r => r.Name));
    }

    [Fact]
    public void ReadAll_StopsAtTheMetadataOffset()
    {
        var first = RdbFixtures.Game("One");
        var db = WriteDb(
            new[] { first, RdbFixtures.Game("Two") },
            metadataOffset: (ulong)(16 + first.Length));

        Assert.Equal("One", Assert.Single(RdbReader.ReadAll(db)).Name);
    }

    [Fact]
    public void ReadAll_FallsBackToTheWholeFileWhenTheOffsetIsZero()
    {
        var db = WriteDb(new[] { RdbFixtures.Game("One") }, metadataOffset: 0);

        Assert.Equal("One", Assert.Single(RdbReader.ReadAll(db)).Name);
    }

    [Fact]
    public void ReadAll_AnswersEmptyForAFileThatIsNotAnRdb()
    {
        var path = NewFile("wrong-magic.rdb");
        File.WriteAllBytes(path, new byte[32]);

        Assert.Empty(RdbReader.ReadAll(path));
    }

    [Fact]
    public void ReadAll_AnswersEmptyForAFileTooShortToHaveAHeader()
    {
        var path = NewFile("stub.rdb");
        File.WriteAllBytes(path, "RARCHDB\0"u8.ToArray());

        Assert.Empty(RdbReader.ReadAll(path));
    }

    [Fact]
    public void ReadAll_SkipsAValueThatIsNotAMap()
    {
        var db = WriteDb(new[] { new MsgPack().Bool(true).ToArray(), RdbFixtures.Game("One") });

        Assert.Equal("One", Assert.Single(RdbReader.ReadAll(db)).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
