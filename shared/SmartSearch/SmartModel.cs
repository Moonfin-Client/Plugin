namespace Moonfin.SmartSearch;

/// <summary>An embedding model smart search can run, pinned to one revision with a hash for every file.</summary>
public sealed class SmartModel
{
    public static readonly SmartModel English = new SmartModel(
        "english",
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41/",
        new[]
        {
            ("config.json", "953f9c0d463486b10a6871cc2fd59f223b2c70184f49815e7efbcab5d8908b41"),
            ("vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
            ("model.safetensors", "53aa51172d142c89d9012cce15ae4d6cc0ca6895895114379cacb4fab128d9db"),
        },
        string.Empty,
        string.Empty,
        0,
        1);

    // e5 models were trained with these prefixes and match noticeably worse without them. They also
    // score nearly everything between 0.7 and 0.85, so similarity is stretched onto the English
    // model's range, which the ranking weights were tuned for. The two ranges were measured on the
    // same queries over the TMDB catalog.
    public static readonly SmartModel Multilingual = new SmartModel(
        "multilingual",
        "https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/",
        new[]
        {
            ("config.json", "69137736cab8b8903a07fe8afaafdda25aac55415a12a55d1bffa9f581abf959"),
            ("tokenizer.json", "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"),
            ("model.safetensors", "1a55775f53449dac10a2bcbc312469fac40b96d53198c407081a831f81c98477"),
        },
        "query: ",
        "passage: ",
        0.715f,
        4.6f);

    private SmartModel(string id, string baseUrl, (string Name, string Sha256)[] files, string queryPrefix, string passagePrefix, float similarityOffset, float similarityScale)
    {
        Id = id;
        BaseUrl = baseUrl;
        Files = files;
        QueryPrefix = queryPrefix;
        PassagePrefix = passagePrefix;
        SimilarityOffset = similarityOffset;
        SimilarityScale = similarityScale;
    }

    /// <summary>The name the admin setting stores.</summary>
    public string Id { get; }

    public string BaseUrl { get; }

    public (string Name, string Sha256)[] Files { get; }

    public string QueryPrefix { get; }

    public string PassagePrefix { get; }

    public float SimilarityOffset { get; }

    public float SimilarityScale { get; }

    /// <summary>The model a setting names, English for anything else.</summary>
    public static SmartModel Named(string? id) => id == Multilingual.Id ? Multilingual : English;
}
