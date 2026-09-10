using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>
/// Validates uploaded theme payloads against the cross-client schema contract.
/// </summary>
public sealed class MoonfinThemeValidator
{
    private static readonly Regex ThemeIdRegex = new("^[a-z0-9_-]{2,40}$", RegexOptions.Compiled);
    private static readonly Regex HexColorRegex = new("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    private static readonly string[] RequiredColorKeys =
    [
        "background", "onBackground", "surface", "onSurface", "surfaceVariant", "scrim",
        "accent", "onAccent", "buttonNormal", "buttonFocused", "buttonDisabled", "buttonActive",
        "onButtonNormal", "onButtonFocused", "onButtonDisabled", "inputBackground", "inputFocused",
        "inputBorder", "inputBorderFocused", "rangeTrack", "rangeProgress", "rangeThumb", "seekbarBuffered",
        "badgeBackground", "onBadge", "badgeUnplayed", "badgeWatched", "recordingActive", "recordingScheduled"
    ];

    private static readonly string[] RequiredSemanticKeys =
    [
        "statusAvailable", "statusRequested", "statusPending", "statusDownloading",
        "mediaTypeBadgeMovie", "mediaTypeBadgeShow"
    ];

    private static readonly string[] RequiredBookColorKeys =
    [
        "background", "accent", "mutedText", "primaryText", "sectionTitle", "divider",
        "placeholder", "shadow", "gradientTop", "gradientBottom", "inactiveChip"
    ];

    private static readonly string[] RadiusCornerKeys = ["topLeft", "topRight", "bottomLeft", "bottomRight"];

    // Optional fields the lists above don't cover. A wrong type still breaks theme
    // loading, and the flags go through a hard cast, so they throw rather than fall back.
    private static readonly string[] OptionalColorKeys = ["error", "card"];

    private static readonly string[] OptionalFlagKeys = ["transparentNavbarSurface", "isGlass", "isPixel"];

    /// <summary>
    /// Validate a candidate theme JSON object.
    /// </summary>
    public MoonfinThemeValidationResult Validate(JsonElement payload)
    {
        var errors = new List<string>();

        if (payload.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Theme payload must be a JSON object.");
            return new MoonfinThemeValidationResult(false, errors, string.Empty, string.Empty);
        }

        if (TryGetProperty(payload, "schemaVersion", out var schemaVersionElement))
        {
            if (schemaVersionElement.ValueKind != JsonValueKind.Number || !schemaVersionElement.TryGetInt32(out var schemaVersion))
            {
                errors.Add("schemaVersion must be an integer.");
            }
            else if (schemaVersion > 1)
            {
                errors.Add("schemaVersion must be 1 or lower.");
            }
        }

        var themeId = GetRequiredString(payload, "id", "id", errors).Trim();
        if (themeId.Length > 0 && !ThemeIdRegex.IsMatch(themeId))
        {
            errors.Add("id must match ^[a-z0-9_-]{2,40}$.");
        }

        var displayName = GetRequiredString(payload, "displayName", "displayName", errors).Trim();
        if (ContainsScriptTag(displayName))
        {
            errors.Add("displayName cannot contain script tags.");
        }

        ValidateOptionalString(payload, "description", "description", errors);
        if (TryGetProperty(payload, "description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
        {
            var description = descriptionElement.GetString() ?? string.Empty;
            if (ContainsScriptTag(description))
            {
                errors.Add("description cannot contain script tags.");
            }
        }

        ValidateOptionalString(payload, "fontFamily", "fontFamily", errors);

        foreach (var key in OptionalFlagKeys)
        {
            ValidateOptionalBool(payload, key, key, errors);
        }

        var colors = GetRequiredObject(payload, "colors", "colors", errors);
        if (colors is { } colorsObject)
        {
            foreach (var key in RequiredColorKeys)
            {
                ValidateRequiredColor(colorsObject, key, "colors." + key, errors);
            }

            foreach (var key in OptionalColorKeys)
            {
                ValidateOptionalColor(colorsObject, key, "colors." + key, errors);
            }
        }

        var semantic = GetRequiredObject(payload, "semantic", "semantic", errors);
        if (semantic is { } semanticObject)
        {
            foreach (var key in RequiredSemanticKeys)
            {
                ValidateRequiredColor(semanticObject, key, "semantic." + key, errors);
            }

            ValidateOptionalColor(semanticObject, "statusError", "semantic.statusError", errors);
        }

        var book = GetRequiredObject(payload, "book", "book", errors);
        if (book is { } bookObject)
        {
            foreach (var key in RequiredBookColorKeys)
            {
                ValidateRequiredColor(bookObject, key, "book." + key, errors);
            }

            if (!TryGetProperty(bookObject, "placeholderPalette", out var placeholderPalette))
            {
                errors.Add("book.placeholderPalette is required.");
            }
            else if (placeholderPalette.ValueKind != JsonValueKind.Array)
            {
                errors.Add("book.placeholderPalette must be an array.");
            }
            else
            {
                var count = placeholderPalette.GetArrayLength();
                if (count < 1 || count > 16)
                {
                    errors.Add("book.placeholderPalette must contain 1 to 16 colors.");
                }

                var paletteIndex = 0;
                foreach (var color in placeholderPalette.EnumerateArray())
                {
                    ValidateColorElement(color, $"book.placeholderPalette[{paletteIndex}]", errors);
                    paletteIndex++;
                }
            }
        }

        var borders = GetRequiredObject(payload, "borders", "borders", errors);
        if (borders is { } bordersObject)
        {
            ValidateRequiredBorder(bordersObject, "cardBorder", "borders.cardBorder", errors);
            ValidateRequiredBorder(bordersObject, "chipBorder", "borders.chipBorder", errors);
            ValidateRequiredBorder(bordersObject, "focusBorder", "borders.focusBorder", errors);
            ValidateRadius(bordersObject, "cardRadius", "borders.cardRadius", errors);
            ValidateRadius(bordersObject, "chipRadius", "borders.chipRadius", errors);
            ValidateRequiredColor(bordersObject, "chipBackground", "borders.chipBackground", errors);

            if (TryGetProperty(bordersObject, "navBorder", out var navBorder) && navBorder.ValueKind != JsonValueKind.Null)
            {
                if (navBorder.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("borders.navBorder must be an object when provided.");
                }
                else
                {
                    ValidateBorderObject(navBorder, "borders.navBorder", errors);
                }
            }

            if (!TryGetProperty(bordersObject, "focusGlow", out var focusGlow))
            {
                errors.Add("borders.focusGlow is required.");
            }
            else
            {
                ValidateShadowArray(focusGlow, "borders.focusGlow", allowSpread: true, errors);
            }
        }

        if (TryGetProperty(payload, "textGlow", out var textGlow))
        {
            ValidateShadowArray(textGlow, "textGlow", allowSpread: false, errors);
        }

        if (TryGetProperty(payload, "navColorCycle", out var navColorCycle))
        {
            if (navColorCycle.ValueKind != JsonValueKind.Array)
            {
                errors.Add("navColorCycle must be an array when provided.");
            }
            else
            {
                var count = navColorCycle.GetArrayLength();
                if (count > 16)
                {
                    errors.Add("navColorCycle must contain at most 16 colors.");
                }

                var index = 0;
                foreach (var color in navColorCycle.EnumerateArray())
                {
                    ValidateColorElement(color, $"navColorCycle[{index}]", errors);
                    index++;
                }
            }
        }

        return new MoonfinThemeValidationResult(
            errors.Count == 0,
            errors,
            themeId,
            displayName);
    }

    private static void ValidateShadowArray(JsonElement arrayElement, string fieldPath, bool allowSpread, List<string> errors)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add(fieldPath + " must be an array.");
            return;
        }

        var count = arrayElement.GetArrayLength();
        if (count > 8)
        {
            errors.Add(fieldPath + " must contain at most 8 entries.");
        }

        var index = 0;
        foreach (var entry in arrayElement.EnumerateArray())
        {
            var entryPath = string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", fieldPath, index);
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add(entryPath + " must be an object.");
                index++;
                continue;
            }

            if (!TryGetProperty(entry, "color", out var colorElement))
            {
                errors.Add(entryPath + ".color is required.");
            }
            else
            {
                ValidateColorElement(colorElement, entryPath + ".color", errors);
            }

            ValidateRequiredNumber(entry, "blurRadius", entryPath + ".blurRadius", 0, 64, errors);
            ValidateRequiredNumber(entry, "offsetX", entryPath + ".offsetX", -500, 500, errors);
            ValidateRequiredNumber(entry, "offsetY", entryPath + ".offsetY", -500, 500, errors);

            if (TryGetProperty(entry, "spreadRadius", out var spreadElement))
            {
                if (!allowSpread)
                {
                    if (spreadElement.ValueKind != JsonValueKind.Number || !spreadElement.TryGetDouble(out var spreadValue) || Math.Abs(spreadValue) > double.Epsilon)
                    {
                        errors.Add(entryPath + ".spreadRadius must be 0 for textGlow.");
                    }
                }
                else
                {
                    ValidateNumberElement(spreadElement, entryPath + ".spreadRadius", -32, 32, errors);
                }
            }
            else if (allowSpread)
            {
                errors.Add(entryPath + ".spreadRadius is required.");
            }

            index++;
        }
    }

    private static void ValidateRequiredBorder(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (!TryGetProperty(owner, propertyName, out var borderElement))
        {
            errors.Add(fieldPath + " is required.");
            return;
        }

        if (borderElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(fieldPath + " must be an object.");
            return;
        }

        ValidateBorderObject(borderElement, fieldPath, errors);
    }

    private static void ValidateBorderObject(JsonElement borderElement, string fieldPath, List<string> errors)
    {
        if (!TryGetProperty(borderElement, "color", out var colorElement))
        {
            errors.Add(fieldPath + ".color is required.");
        }
        else
        {
            ValidateColorElement(colorElement, fieldPath + ".color", errors);
        }

        ValidateRequiredNumber(borderElement, "width", fieldPath + ".width", 0, 16, errors);
    }

    private static void ValidateRadius(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (!TryGetProperty(owner, propertyName, out var radiusElement))
        {
            errors.Add(fieldPath + " is required.");
            return;
        }

        if (radiusElement.ValueKind == JsonValueKind.Number)
        {
            ValidateNumberElement(radiusElement, fieldPath, 0, 9999, errors);
            return;
        }

        if (radiusElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(fieldPath + " must be a number or corner object.");
            return;
        }

        foreach (var corner in RadiusCornerKeys)
        {
            ValidateRequiredNumber(radiusElement, corner, fieldPath + "." + corner, 0, 9999, errors);
        }
    }

    private static void ValidateRequiredNumber(JsonElement owner, string propertyName, string fieldPath, double min, double max, List<string> errors)
    {
        if (!TryGetProperty(owner, propertyName, out var numberElement))
        {
            errors.Add(fieldPath + " is required.");
            return;
        }

        ValidateNumberElement(numberElement, fieldPath, min, max, errors);
    }

    private static void ValidateNumberElement(JsonElement numberElement, string fieldPath, double min, double max, List<string> errors)
    {
        if (numberElement.ValueKind != JsonValueKind.Number || !numberElement.TryGetDouble(out var numericValue))
        {
            errors.Add(fieldPath + " must be a number.");
            return;
        }

        if (numericValue < min || numericValue > max)
        {
            errors.Add(fieldPath + " must be between " + min.ToString(CultureInfo.InvariantCulture) + " and " + max.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    private static void ValidateRequiredColor(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (!TryGetProperty(owner, propertyName, out var valueElement))
        {
            errors.Add(fieldPath + " is required.");
            return;
        }

        ValidateColorElement(valueElement, fieldPath, errors);
    }

    private static void ValidateOptionalColor(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (TryGetProperty(owner, propertyName, out var valueElement))
        {
            ValidateColorElement(valueElement, fieldPath, errors);
        }
    }

    private static void ValidateOptionalBool(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (TryGetProperty(owner, propertyName, out var valueElement)
            && valueElement.ValueKind != JsonValueKind.True
            && valueElement.ValueKind != JsonValueKind.False)
        {
            errors.Add(fieldPath + " must be true or false.");
        }
    }

    private static void ValidateOptionalString(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (TryGetProperty(owner, propertyName, out var valueElement) && valueElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(fieldPath + " must be a string.");
        }
    }

    private static void ValidateColorElement(JsonElement colorElement, string fieldPath, List<string> errors)
    {
        if (colorElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(fieldPath + " must be a string color (#RRGGBB or #AARRGGBB).");
            return;
        }

        var value = colorElement.GetString() ?? string.Empty;
        if (!HexColorRegex.IsMatch(value))
        {
            errors.Add(fieldPath + " must be a valid color (#RRGGBB or #AARRGGBB).");
        }
    }

    private static string GetRequiredString(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (!TryGetProperty(owner, propertyName, out var valueElement))
        {
            errors.Add(fieldPath + " is required.");
            return string.Empty;
        }

        if (valueElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(fieldPath + " must be a string.");
            return string.Empty;
        }

        var value = valueElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(fieldPath + " cannot be empty.");
            return string.Empty;
        }

        return value;
    }

    private static JsonElement? GetRequiredObject(JsonElement owner, string propertyName, string fieldPath, List<string> errors)
    {
        if (!TryGetProperty(owner, propertyName, out var valueElement))
        {
            errors.Add(fieldPath + " is required.");
            return null;
        }

        if (valueElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(fieldPath + " must be an object.");
            return null;
        }

        return valueElement;
    }

    private static bool TryGetProperty(JsonElement owner, string propertyName, out JsonElement value)
    {
        if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool ContainsScriptTag(string value)
    {
        return value.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || value.Contains("</script", StringComparison.OrdinalIgnoreCase);
    }
}
