using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Jellyfin writes the config with the legacy XmlTextWriter, which validates no characters, and
/// reads it back with a strict XML 1.0 reader. A value that survives the write but not the read
/// leaves the file unreadable, and Jellyfin answers that by overwriting it with defaults. These
/// pin the round trip against that exact pair, building characters from code points so this
/// source file holds none of them literally.
/// </summary>
public class PluginConfigurationXmlTests
{
    private const int Rocket = 0x1F680;

    // Code points XML 1.0 can't carry. Tab, LF and CR are legal and deliberately absent.
    public static TheoryData<string, int> IllegalCodePoints => new()
    {
        { "null", 0x00 },
        { "backspace", 0x08 },
        { "vertical tab", 0x0B },
        { "form feed", 0x0C },
        { "shift out", 0x0E },
        { "escape", 0x1B },
        { "unit separator", 0x1F },
        { "lone high surrogate", 0xD83D },
        { "lone low surrogate", 0xDE00 },
    };

    /// <summary>Mirrors MyXmlSerializer.SerializeToStream.</summary>
    private static byte[] Serialize(PluginConfiguration config)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, null, 1024, leaveOpen: true))
        using (var textWriter = new XmlTextWriter(writer))
        {
            textWriter.Formatting = Formatting.Indented;
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(textWriter, config);
        }

        return stream.ToArray();
    }

    /// <summary>Mirrors MyXmlSerializer.DeserializeFromStream.</summary>
    private static PluginConfiguration Deserialize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
    }

    private static PluginConfiguration WithMessageBody(string body)
    {
        var config = new PluginConfiguration();
        var message = new ServerMessage
        {
            Id = "m1",
            Title = "Maintenance",
            Body = body,
            CreatedUtc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)
        };
        message.Sanitize();
        config.Messages.Add(message);
        return config;
    }

    [Theory]
    [MemberData(nameof(IllegalCodePoints))]
    public void SanitizedMessageBodySurvivesTheConfigRoundTrip(string name, int codePoint)
    {
        _ = name;
        var body = "Server reboot at 9pm" + (char)codePoint + " sorry for the noise";

        var read = Deserialize(Serialize(WithMessageBody(body)));

        Assert.Single(read.Messages);
        Assert.Contains("Server reboot at 9pm", read.Messages[0].Body, StringComparison.Ordinal);
        Assert.Contains("sorry for the noise", read.Messages[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(IllegalCodePoints))]
    public void SanitizedMessageTitleSurvivesTheConfigRoundTrip(string name, int codePoint)
    {
        _ = name;
        var config = WithMessageBody("body");
        config.Messages[0].Title = "Scheduled" + (char)codePoint + " maintenance";
        config.Messages[0].Sanitize();

        var read = Deserialize(Serialize(config));

        Assert.Contains("Scheduled", read.Messages[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void LegalWhitespaceInAMessageBodyIsKept()
    {
        var body = "line one" + (char)0x0A + "line two" + (char)0x09 + "tabbed" + (char)0x0D + "line three";

        var read = Deserialize(Serialize(WithMessageBody(body)));

        Assert.Contains("line one", read.Messages[0].Body, StringComparison.Ordinal);
        Assert.Contains("line two", read.Messages[0].Body, StringComparison.Ordinal);
        Assert.Contains("line three", read.Messages[0].Body, StringComparison.Ordinal);
        Assert.Contains((char)0x09, read.Messages[0].Body);
    }

    [Fact]
    public void EmojiInAMessageBodySurvivesTheConfigRoundTrip()
    {
        var rocket = char.ConvertFromUtf32(Rocket);

        var read = Deserialize(Serialize(WithMessageBody("all good " + rocket + " shipping now")));

        Assert.Contains(rocket, read.Messages[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationAtMaxBodyLengthNeverSplitsASurrogatePair()
    {
        // Lands the pair exactly astride the cap, so a naive Substring would keep half of it.
        var body = new StringBuilder()
            .Append('a', ServerMessage.MaxBodyLength - 1)
            .Append(char.ConvertFromUtf32(Rocket))
            .Append("trailing")
            .ToString();

        var read = Deserialize(Serialize(WithMessageBody(body)));

        Assert.True(read.Messages[0].Body.Length <= ServerMessage.MaxBodyLength);
        Assert.DoesNotContain((char)0xD83D, read.Messages[0].Body);
        Assert.DoesNotContain((char)0xDE80, read.Messages[0].Body);
    }
}
