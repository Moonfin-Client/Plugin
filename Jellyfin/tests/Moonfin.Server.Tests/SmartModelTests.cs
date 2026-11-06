using System;
using System.IO;
using Moonfin.SmartSearch;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The multilingual tokenizer's rules on a tiny vocabulary, and what happens when the admin
/// switches models.
/// </summary>
public sealed class SmartModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "smart-model-tests-" + Guid.NewGuid().ToString("N"));

    public SmartModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private UnigramTokenizer Tokenizer()
    {
        var path = Path.Combine(_folder, "tokenizer.json");
        File.WriteAllText(path, """
            {"normalizer": {"type": "Sequence", "normalizers": []},
             "model": {"type": "Unigram", "unk_id": 1, "vocab": [
               ["<s>", 0], ["<unk>", 0], ["</s>", 0], ["▁", -2], ["▁hel", -3], ["lo", -3],
               ["▁hello", -7], ["▁wor", -4], ["ld", -4], ["▁world", -5]]}}
            """);
        return new UnigramTokenizer(path);
    }

    [Fact]
    public void TakesTheMostLikelySplitOfEachWord()
    {
        Assert.Equal(new[] { 0, 4, 5, 9, 2 }, Tokenizer().Encode("hello world", 64));
    }

    [Fact]
    public void ZeroWidthSpacesSplitWordsAndATrailingSpaceIsItsOwnToken()
    {
        var tokenizer = Tokenizer();

        Assert.Equal(new[] { 0, 4, 5, 9, 2 }, tokenizer.Encode("hello​world", 64));
        Assert.Equal(new[] { 0, 4, 5, 3, 2 }, tokenizer.Encode("hello ", 64));
    }

    [Fact]
    public void UnknownCharactersInARowBecomeOneToken()
    {
        Assert.Equal(new[] { 0, 3, 1, 2 }, Tokenizer().Encode("héé", 64));
    }

    [Fact]
    public void SwitchingModelsWaitsForARebuild()
    {
        var index = new SmartIndex { Model = SmartModel.English.Id };
        index.Entries.Add(new SmartEntry { Key = "1", Kind = SmartQuery.Movie, Title = "Up", Vector = new float[384] });
        index.Save(Path.Combine(_folder, "smart-search", "library.bin"));
        Directory.CreateDirectory(Path.Combine(_folder, "smart-search", "models", "english"));
        File.WriteAllBytes(Path.Combine(_folder, "smart-search", "models", "english", "model.safetensors"), Array.Empty<byte>());

        var picked = SmartModel.English;
        var engine = new SmartSearchEngine(_folder, () => picked);
        Assert.True(engine.Ready);

        picked = SmartModel.Multilingual;
        Assert.False(engine.Ready);
    }
}
