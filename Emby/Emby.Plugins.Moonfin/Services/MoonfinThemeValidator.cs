using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Emby.Plugins.Moonfin.Models;

namespace Emby.Plugins.Moonfin.Services
{
    public sealed class MoonfinThemeValidator
    {
        private static readonly Regex ThemeIdRegex = new Regex("^[a-z0-9_-]{2,40}$", RegexOptions.Compiled);
        private static readonly Regex HexColorRegex = new Regex("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

        private static readonly string[] RequiredColorKeys =
        {
            "background", "onBackground", "surface", "onSurface", "surfaceVariant", "scrim",
            "accent", "onAccent", "buttonNormal", "buttonFocused", "buttonDisabled", "buttonActive",
            "onButtonNormal", "onButtonFocused", "onButtonDisabled", "inputBackground", "inputFocused",
            "inputBorder", "inputBorderFocused", "rangeTrack", "rangeProgress", "rangeThumb", "seekbarBuffered",
            "badgeBackground", "onBadge", "badgeUnplayed", "badgeWatched", "recordingActive", "recordingScheduled"
        };

        private static readonly string[] RequiredSemanticKeys =
        {
            "statusAvailable", "statusRequested", "statusPending", "statusDownloading",
            "mediaTypeBadgeMovie", "mediaTypeBadgeShow"
        };

        private static readonly string[] RequiredBookColorKeys =
        {
            "background", "accent", "mutedText", "primaryText", "sectionTitle", "divider",
            "placeholder", "shadow", "gradientTop", "gradientBottom", "inactiveChip"
        };

        private static readonly string[] RadiusCornerKeys = { "topLeft", "topRight", "bottomLeft", "bottomRight" };

        // Optional fields the lists above don't cover. A wrong type still breaks theme
        // loading, and the flags go through a hard cast, so they throw rather than fall back.
        private static readonly string[] OptionalColorKeys = { "error", "card" };

        private static readonly string[] OptionalFlagKeys = { "transparentNavbarSurface", "isGlass", "isPixel" };

        public MoonfinThemeValidationResult Validate(JsonElement payload)
        {
            var errors = new List<string>();

            if (payload.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Theme payload must be a JSON object.");
                return new MoonfinThemeValidationResult(false, errors, string.Empty, string.Empty);
            }

            if (TryGetProperty(payload, "schemaVersion", out var schemaVersionEl))
            {
                if (schemaVersionEl.ValueKind != JsonValueKind.Number || !schemaVersionEl.TryGetInt32(out var sv))
                    errors.Add("schemaVersion must be an integer.");
                else if (sv > 1)
                    errors.Add("schemaVersion must be 1 or lower.");
            }

            var themeId = GetRequiredString(payload, "id", "id", errors).Trim();
            if (themeId.Length > 0 && !ThemeIdRegex.IsMatch(themeId))
                errors.Add("id must match ^[a-z0-9_-]{2,40}$.");

            var displayName = GetRequiredString(payload, "displayName", "displayName", errors).Trim();
            if (ContainsScriptTag(displayName))
                errors.Add("displayName cannot contain script tags.");

            ValidateOptionalString(payload, "description", "description", errors);
            if (TryGetProperty(payload, "description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            {
                var desc = descEl.GetString() ?? string.Empty;
                if (ContainsScriptTag(desc)) errors.Add("description cannot contain script tags.");
            }

            ValidateOptionalString(payload, "fontFamily", "fontFamily", errors);

            foreach (var key in OptionalFlagKeys)
                ValidateOptionalBool(payload, key, key, errors);

            var colors = GetRequiredObject(payload, "colors", "colors", errors);
            if (colors.HasValue)
            {
                foreach (var key in RequiredColorKeys)
                    ValidateRequiredColor(colors.Value, key, "colors." + key, errors);

                foreach (var key in OptionalColorKeys)
                    ValidateOptionalColor(colors.Value, key, "colors." + key, errors);
            }

            var semantic = GetRequiredObject(payload, "semantic", "semantic", errors);
            if (semantic.HasValue)
            {
                foreach (var key in RequiredSemanticKeys)
                    ValidateRequiredColor(semantic.Value, key, "semantic." + key, errors);

                ValidateOptionalColor(semantic.Value, "statusError", "semantic.statusError", errors);
            }

            var book = GetRequiredObject(payload, "book", "book", errors);
            if (book.HasValue)
            {
                foreach (var key in RequiredBookColorKeys)
                    ValidateRequiredColor(book.Value, key, "book." + key, errors);

                if (!TryGetProperty(book.Value, "placeholderPalette", out var pp))
                    errors.Add("book.placeholderPalette is required.");
                else if (pp.ValueKind != JsonValueKind.Array)
                    errors.Add("book.placeholderPalette must be an array.");
                else
                {
                    var count = pp.GetArrayLength();
                    if (count < 1 || count > 16) errors.Add("book.placeholderPalette must contain 1 to 16 colors.");
                    var i = 0;
                    foreach (var color in pp.EnumerateArray())
                    { ValidateColorElement(color, $"book.placeholderPalette[{i}]", errors); i++; }
                }
            }

            var borders = GetRequiredObject(payload, "borders", "borders", errors);
            if (borders.HasValue)
            {
                ValidateRequiredBorder(borders.Value, "cardBorder", "borders.cardBorder", errors);
                ValidateRequiredBorder(borders.Value, "chipBorder", "borders.chipBorder", errors);
                ValidateRequiredBorder(borders.Value, "focusBorder", "borders.focusBorder", errors);
                ValidateRadius(borders.Value, "cardRadius", "borders.cardRadius", errors);
                ValidateRadius(borders.Value, "chipRadius", "borders.chipRadius", errors);
                ValidateRequiredColor(borders.Value, "chipBackground", "borders.chipBackground", errors);

                if (TryGetProperty(borders.Value, "navBorder", out var navBorder) && navBorder.ValueKind != JsonValueKind.Null)
                {
                    if (navBorder.ValueKind != JsonValueKind.Object)
                        errors.Add("borders.navBorder must be an object when provided.");
                    else
                        ValidateBorderObject(navBorder, "borders.navBorder", errors);
                }

                if (!TryGetProperty(borders.Value, "focusGlow", out var focusGlow))
                    errors.Add("borders.focusGlow is required.");
                else
                    ValidateShadowArray(focusGlow, "borders.focusGlow", allowSpread: true, errors);
            }

            if (TryGetProperty(payload, "textGlow", out var textGlow))
                ValidateShadowArray(textGlow, "textGlow", allowSpread: false, errors);

            if (TryGetProperty(payload, "navColorCycle", out var ncc))
            {
                if (ncc.ValueKind != JsonValueKind.Array)
                    errors.Add("navColorCycle must be an array when provided.");
                else
                {
                    if (ncc.GetArrayLength() > 16) errors.Add("navColorCycle must contain at most 16 colors.");
                    var i = 0;
                    foreach (var c in ncc.EnumerateArray())
                    { ValidateColorElement(c, $"navColorCycle[{i}]", errors); i++; }
                }
            }

            return new MoonfinThemeValidationResult(errors.Count == 0, errors, themeId, displayName);
        }

        private static void ValidateShadowArray(JsonElement el, string path, bool allowSpread, List<string> errors)
        {
            if (el.ValueKind != JsonValueKind.Array) { errors.Add(path + " must be an array."); return; }
            if (el.GetArrayLength() > 8) errors.Add(path + " must contain at most 8 entries.");
            var i = 0;
            foreach (var entry in el.EnumerateArray())
            {
                var ep = string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", path, i);
                if (entry.ValueKind != JsonValueKind.Object) { errors.Add(ep + " must be an object."); i++; continue; }
                if (!TryGetProperty(entry, "color", out var colorEl)) errors.Add(ep + ".color is required.");
                else ValidateColorElement(colorEl, ep + ".color", errors);
                ValidateRequiredNumber(entry, "blurRadius", ep + ".blurRadius", 0, 64, errors);
                ValidateRequiredNumber(entry, "offsetX", ep + ".offsetX", -500, 500, errors);
                ValidateRequiredNumber(entry, "offsetY", ep + ".offsetY", -500, 500, errors);
                if (TryGetProperty(entry, "spreadRadius", out var sr))
                {
                    if (!allowSpread)
                    {
                        if (sr.ValueKind != JsonValueKind.Number || !sr.TryGetDouble(out var sv) || Math.Abs(sv) > double.Epsilon)
                            errors.Add(ep + ".spreadRadius must be 0 for textGlow.");
                    }
                    else ValidateNumberElement(sr, ep + ".spreadRadius", -32, 32, errors);
                }
                else if (allowSpread) errors.Add(ep + ".spreadRadius is required.");
                i++;
            }
        }

        private static void ValidateRequiredBorder(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (!TryGetProperty(owner, prop, out var el)) { errors.Add(path + " is required."); return; }
            if (el.ValueKind != JsonValueKind.Object) { errors.Add(path + " must be an object."); return; }
            ValidateBorderObject(el, path, errors);
        }

        private static void ValidateBorderObject(JsonElement el, string path, List<string> errors)
        {
            if (!TryGetProperty(el, "color", out var c)) errors.Add(path + ".color is required.");
            else ValidateColorElement(c, path + ".color", errors);
            ValidateRequiredNumber(el, "width", path + ".width", 0, 16, errors);
        }

        private static void ValidateRadius(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (!TryGetProperty(owner, prop, out var el)) { errors.Add(path + " is required."); return; }
            if (el.ValueKind == JsonValueKind.Number) { ValidateNumberElement(el, path, 0, 9999, errors); return; }
            if (el.ValueKind != JsonValueKind.Object) { errors.Add(path + " must be a number or corner object."); return; }
            foreach (var corner in RadiusCornerKeys)
                ValidateRequiredNumber(el, corner, path + "." + corner, 0, 9999, errors);
        }

        private static void ValidateRequiredNumber(JsonElement owner, string prop, string path, double min, double max, List<string> errors)
        {
            if (!TryGetProperty(owner, prop, out var el)) { errors.Add(path + " is required."); return; }
            ValidateNumberElement(el, path, min, max, errors);
        }

        private static void ValidateNumberElement(JsonElement el, string path, double min, double max, List<string> errors)
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v)) { errors.Add(path + " must be a number."); return; }
            if (v < min || v > max) errors.Add($"{path} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        private static void ValidateRequiredColor(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (!TryGetProperty(owner, prop, out var el)) { errors.Add(path + " is required."); return; }
            ValidateColorElement(el, path, errors);
        }

        private static void ValidateOptionalColor(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (TryGetProperty(owner, prop, out var el)) ValidateColorElement(el, path, errors);
        }

        private static void ValidateOptionalBool(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (TryGetProperty(owner, prop, out var el)
                && el.ValueKind != JsonValueKind.True
                && el.ValueKind != JsonValueKind.False)
            {
                errors.Add(path + " must be true or false.");
            }
        }

        private static void ValidateOptionalString(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (TryGetProperty(owner, prop, out var el) && el.ValueKind != JsonValueKind.String)
                errors.Add(path + " must be a string.");
        }

        private static void ValidateColorElement(JsonElement el, string path, List<string> errors)
        {
            if (el.ValueKind != JsonValueKind.String) { errors.Add(path + " must be a string color (#RRGGBB or #AARRGGBB)."); return; }
            var v = el.GetString() ?? string.Empty;
            if (!HexColorRegex.IsMatch(v)) errors.Add(path + " must be a valid color (#RRGGBB or #AARRGGBB).");
        }

        private static string GetRequiredString(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (!TryGetProperty(owner, prop, out var el)) { errors.Add(path + " is required."); return string.Empty; }
            if (el.ValueKind != JsonValueKind.String) { errors.Add(path + " must be a string."); return string.Empty; }
            var v = el.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(v)) { errors.Add(path + " cannot be empty."); return string.Empty; }
            return v;
        }

        private static JsonElement? GetRequiredObject(JsonElement owner, string prop, string path, List<string> errors)
        {
            if (!TryGetProperty(owner, prop, out var el)) { errors.Add(path + " is required."); return null; }
            if (el.ValueKind != JsonValueKind.Object) { errors.Add(path + " must be an object."); return null; }
            return el;
        }

        private static bool TryGetProperty(JsonElement owner, string prop, out JsonElement value)
        {
            if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(prop, out value)) return true;
            value = default;
            return false;
        }

        private static bool ContainsScriptTag(string value) =>
            value.IndexOf("<script", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("</script", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
