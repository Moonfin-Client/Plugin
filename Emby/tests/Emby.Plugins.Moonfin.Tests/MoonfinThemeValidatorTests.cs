using System.Text.Json;
using System.Text.Json.Nodes;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The validator is what stands between an uploaded theme and the config, so every rule it
/// enforces is a rule nobody else checks.
/// </summary>
public class MoonfinThemeValidatorTests
{
    [Fact]
    public void Validate_AcceptsACompleteTheme()
    {
        var result = ThemeFixture.Validate(ThemeFixture.Valid());

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Equal("midnight-blue", result.ThemeId);
        Assert.Equal("Midnight Blue", result.DisplayName);
    }

    [Fact]
    public void Validate_RejectsAPayloadThatIsNotAnObject()
    {
        var result = new MoonfinThemeValidator().Validate(
            JsonSerializer.Deserialize<JsonElement>("[1,2,3]"));

        Assert.False(result.IsValid);
        Assert.Equal("Theme payload must be a JSON object.", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("Midnight")]
    [InlineData("a")]
    [InlineData("has spaces")]
    [InlineData("Punctuation!")]
    public void Validate_RejectsAnIdThatBreaksThePattern(string id)
    {
        var result = ThemeFixture.ValidateWith(t => t["id"] = id);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("id must match", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAnIdOfFortyOneCharacters()
    {
        var result = ThemeFixture.ValidateWith(t => t["id"] = new string('a', 41));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsAnIdAtBothEndsOfTheRange()
    {
        Assert.True(ThemeFixture.ValidateWith(t => t["id"] = "ab").IsValid);
        Assert.True(ThemeFixture.ValidateWith(t => t["id"] = new string('a', 40)).IsValid);
    }

    [Fact]
    public void Validate_RejectsAMissingOrEmptyRequiredString()
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t.Remove("displayName")).Errors,
            e => e == "displayName is required.");
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["displayName"] = "   ").Errors,
            e => e == "displayName cannot be empty.");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("safe </SCRIPT> tail")]
    public void Validate_RejectsScriptTagsInTextTheUiRenders(string text)
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["displayName"] = text).Errors,
            e => e == "displayName cannot contain script tags.");
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["description"] = text).Errors,
            e => e == "description cannot contain script tags.");
    }

    [Fact]
    public void Validate_AcceptsSchemaVersionUpToOneAndRejectsHigher()
    {
        Assert.True(ThemeFixture.ValidateWith(t => t["schemaVersion"] = 1).IsValid);
        Assert.True(ThemeFixture.ValidateWith(t => t.Remove("schemaVersion")).IsValid);
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["schemaVersion"] = 2).Errors,
            e => e == "schemaVersion must be 1 or lower.");
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["schemaVersion"] = "one").Errors,
            e => e == "schemaVersion must be an integer.");
    }

    [Theory]
    [InlineData("colors", "background")]
    [InlineData("semantic", "statusAvailable")]
    [InlineData("book", "gradientTop")]
    public void Validate_RejectsAMissingColourFromEveryRequiredGroup(string group, string key)
    {
        var result = ThemeFixture.ValidateWith(t => t[group]!.AsObject().Remove(key));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e == $"{group}.{key} is required.");
    }

    [Theory]
    [InlineData("112233")]
    [InlineData("#112")]
    [InlineData("#GGHHII")]
    [InlineData("rgb(1,2,3)")]
    public void Validate_RejectsAColourThatIsNotHex(string colour)
    {
        var result = ThemeFixture.ValidateWith(t => t["colors"]!["background"] = colour);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith("colors.background must be a valid", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsBothColourLengths()
    {
        Assert.True(ThemeFixture.ValidateWith(t => t["colors"]!["background"] = "#112233").IsValid);
        Assert.True(ThemeFixture.ValidateWith(t => t["colors"]!["background"] = "#FF112233").IsValid);
    }

    [Fact]
    public void Validate_RequiresAWholeGroupToBeAnObject()
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t.Remove("colors")).Errors,
            e => e == "colors is required.");
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"] = 7).Errors,
            e => e == "borders must be an object.");
    }

    [Fact]
    public void Validate_BoundsThePlaceholderPalette()
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["book"]!["placeholderPalette"] = new JsonArray()).Errors,
            e => e == "book.placeholderPalette must contain 1 to 16 colors.");

        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["book"]!["placeholderPalette"] = ThemeFixture.Palette(17)).Errors,
            e => e == "book.placeholderPalette must contain 1 to 16 colors.");
    }

    [Fact]
    public void Validate_NamesTheOffendingIndexInAPalette()
    {
        var result = ThemeFixture.ValidateWith(
            t => t["book"]!["placeholderPalette"] = new JsonArray("#112233", "nope"));

        Assert.Contains(result.Errors, e => e.StartsWith("book.placeholderPalette[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BoundsABorderWidth()
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["cardBorder"]!["width"] = 17).Errors,
            e => e == "borders.cardBorder.width must be between 0 and 16.");
        Assert.True(ThemeFixture.ValidateWith(t => t["borders"]!["cardBorder"]!["width"] = 16).IsValid);
    }

    [Fact]
    public void Validate_TakesARadiusAsANumberOrACornerObject()
    {
        Assert.True(ThemeFixture.ValidateWith(t => t["borders"]!["cardRadius"] = 12).IsValid);

        Assert.True(ThemeFixture.ValidateWith(t => t["borders"]!["cardRadius"] = new JsonObject
        {
            ["topLeft"] = 1,
            ["topRight"] = 2,
            ["bottomLeft"] = 3,
            ["bottomRight"] = 4,
        }).IsValid);

        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["cardRadius"] = new JsonObject { ["topLeft"] = 1 }).Errors,
            e => e == "borders.cardRadius.topRight is required.");

        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["cardRadius"] = "round").Errors,
            e => e == "borders.cardRadius must be a number or corner object.");
    }

    [Fact]
    public void Validate_TreatsNavBorderAsOptionalButCheckedWhenPresent()
    {
        Assert.True(ThemeFixture.ValidateWith(t => t["borders"]!.AsObject().Remove("navBorder")).IsValid);
        Assert.True(ThemeFixture.ValidateWith(t => t["borders"]!["navBorder"] = null).IsValid);
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["navBorder"] = new JsonObject { ["width"] = 1 }).Errors,
            e => e == "borders.navBorder.color is required.");
    }

    [Fact]
    public void Validate_CapsAShadowArrayAtEightEntries()
    {
        var nine = new JsonArray();
        for (var i = 0; i < 9; i++)
        {
            nine.Add(ThemeFixture.Shadow(1));
        }

        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["focusGlow"] = nine).Errors,
            e => e == "borders.focusGlow must contain at most 8 entries.");
    }

    [Fact]
    public void Validate_BoundsTheNumbersInAShadow()
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["focusGlow"]![0]!["blurRadius"] = 65).Errors,
            e => e == "borders.focusGlow[0].blurRadius must be between 0 and 64.");
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["focusGlow"]![0]!["offsetX"] = 501).Errors,
            e => e == "borders.focusGlow[0].offsetX must be between -500 and 500.");
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["focusGlow"]![0]!["spreadRadius"] = 33).Errors,
            e => e == "borders.focusGlow[0].spreadRadius must be between -32 and 32.");
    }

    [Fact]
    public void Validate_RequiresASpreadOnFocusGlowAndForbidsOneOnTextGlow()
    {
        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["borders"]!["focusGlow"]![0]!.AsObject().Remove("spreadRadius")).Errors,
            e => e == "borders.focusGlow[0].spreadRadius is required.");

        Assert.True(ThemeFixture.ValidateWith(t => t["textGlow"] = new JsonArray(ThemeFixture.Shadow())).IsValid);

        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["textGlow"] = new JsonArray(ThemeFixture.Shadow(3))).Errors,
            e => e == "textGlow[0].spreadRadius must be 0 for textGlow.");
    }

    [Fact]
    public void Validate_CapsTheNavColourCycle()
    {
        Assert.True(ThemeFixture.ValidateWith(t => t["navColorCycle"] = new JsonArray("#112233")).IsValid);

        Assert.Contains(
            ThemeFixture.ValidateWith(t => t["navColorCycle"] = ThemeFixture.Palette(17)).Errors,
            e => e == "navColorCycle must contain at most 16 colors.");
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        // A theme editor shows the whole list, so the validator must not stop at the first error.
        var result = ThemeFixture.ValidateWith(t =>
        {
            t["id"] = "Nope";
            t["colors"]!["background"] = "red";
            t.Remove("semantic");
        });

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3, string.Join(" | ", result.Errors));
    }

    [Theory]
    [InlineData("transparentNavbarSurface")]
    [InlineData("isGlass")]
    [InlineData("isPixel")]
    public void Validate_RejectsAFlagThatIsNotABoolean(string key)
    {
        var result = ThemeFixture.ValidateWith(t => t[key] = "true");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e == key + " must be true or false.");
    }

    [Theory]
    [InlineData("transparentNavbarSurface")]
    [InlineData("isGlass")]
    [InlineData("isPixel")]
    public void Validate_AcceptsAFlagThatIsABoolean(string key)
    {
        Assert.True(ThemeFixture.ValidateWith(t => t[key] = true).IsValid);
        Assert.True(ThemeFixture.ValidateWith(t => t[key] = false).IsValid);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("card")]
    public void Validate_RejectsAnOptionalColorThatIsMalformed(string key)
    {
        var result = ThemeFixture.ValidateWith(t => t["colors"]![key] = "notacolor");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith("colors." + key, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAMalformedStatusError()
    {
        var result = ThemeFixture.ValidateWith(t => t["semantic"]!["statusError"] = "nope");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith("semantic.statusError", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsTheOptionalColorsWhenTheyAreWellFormed()
    {
        var result = ThemeFixture.ValidateWith(t =>
        {
            t["colors"]!["error"] = "#FF0000";
            t["colors"]!["card"] = "#112233AA";
            t["semantic"]!["statusError"] = "#EF4444";
        });

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("fontFamily")]
    public void Validate_RejectsAnOptionalStringThatIsNotAString(string key)
    {
        var result = ThemeFixture.ValidateWith(t => t[key] = 42);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e == key + " must be a string.");
    }
}
