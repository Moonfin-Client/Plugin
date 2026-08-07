using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moonfin.Server.Api;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Keeps the additive controller surface explicit without constructing server-only Jellyfin
/// services. Request/response behavior remains covered by the artwork catalog and worker tests.
/// </summary>
public sealed class GamesControllerArtworkContractTests
{
    [Fact]
    public void ArtworkCapabilities_AdvertisesProtocolTwoAndEveryFeature()
    {
        var controller = (GamesController)RuntimeHelpers.GetUninitializedObject(typeof(GamesController));

        var response = Assert.IsType<OkObjectResult>(controller.GetArtworkCapabilities().Result);
        var capabilities = Assert.IsType<GameArtworkCapabilities>(response.Value);

        Assert.Equal(2, capabilities.ProtocolVersion);
        Assert.True(capabilities.Manifest);
        Assert.True(capabilities.VersionedAssets);
        Assert.True(capabilities.PriorityHints);
        Assert.True(capabilities.SystemPreviews);
    }

    [Theory]
    [InlineData(nameof(GamesController.GetArtworkManifest), "{libraryId}/ArtworkManifest")]
    [InlineData(nameof(GamesController.PostArtworkPriority), "{libraryId}/ArtworkPriority")]
    [InlineData(nameof(GamesController.GetArtwork), "{libraryId}/Artwork/{gameId}/{role}/{revision}")]
    [InlineData(nameof(GamesController.RefreshArtwork), "{libraryId}/Artwork/{gameId}/Refresh")]
    public void ArtworkActions_KeepTheirAdditiveRouteTemplates(string actionName, string expectedTemplate)
    {
        var method = typeof(GamesController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing {actionName} action.");
        var route = method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true)
            .Cast<HttpMethodAttribute>()
            .Single();

        Assert.Equal(expectedTemplate, route.Template);
    }

    // --- Artwork cache-control contract -----------------------------------------------------
    //
    // ServeArtworkAsync backs both GetArtwork (versioned, {revision} in the route) and GetThumb
    // (legacy, unversioned route -- no revision segment). A full request-path test is
    // impractical here: the controller is constructed via RuntimeHelpers.GetUninitializedObject
    // because Jellyfin's service graph resists mocking (see the class doc above), so
    // GamesController.SelectArtworkCacheControl -- the pure header-selection helper
    // ServeArtworkAsync delegates to -- is exercised directly instead.

    [Fact]
    public void SelectArtworkCacheControl_VersionedThumbnail_IsImmutableForOneYear()
    {
        Assert.Equal(
            "private,max-age=31536000,immutable",
            GamesController.SelectArtworkCacheControl(versionedUrl: true, isThumbnail: true));
    }

    [Fact]
    public void SelectArtworkCacheControl_VersionedFullResolution_IsShortLivedButNotImmutable()
    {
        Assert.Equal(
            "private,max-age=7200",
            GamesController.SelectArtworkCacheControl(versionedUrl: true, isThumbnail: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectArtworkCacheControl_LegacyUnversionedRoute_AlwaysRevalidates(bool isThumbnail)
    {
        // GetThumb's route has no {revision} segment, so an admin's Artwork/{gameId}/Refresh
        // must be visible to legacy clients promptly -- never "immutable" for a year regardless
        // of whether the served asset happens to be a thumbnail or the full-resolution original.
        Assert.Equal(
            "private,max-age=300,must-revalidate",
            GamesController.SelectArtworkCacheControl(versionedUrl: false, isThumbnail: isThumbnail));
    }

    // --- Authorization contract -------------------------------------------------------------
    //
    // The tests above assert route *templates* only; nothing previously inspected
    // [Authorize]/[AllowAnonymous]. Dropping [Authorize] from GetArtwork, or downgrading
    // RefreshArtwork from RequiresElevation to plain [Authorize], would have passed the whole
    // suite. These tests pin the authorization policy of every public action on GamesController
    // and EmulatorController via reflection alone (no controller instance, no DI), and fail
    // if a newly added action has no declared expectation here.

    /// <summary>
    /// The full, deliberately exhaustive map of GamesController action name -> expected
    /// [Authorize] policy (null means plain [Authorize] with no named policy). Every public
    /// action on the controller must have an entry here; <see cref="GamesController_NoActionIsMissingAnAuthorizationExpectation"/>
    /// enforces that a new endpoint cannot ship without someone consciously declaring its policy.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> ExpectedGamesControllerPolicies =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(GamesController.Debug)] = "RequiresElevation",
            [nameof(GamesController.GetLibraries)] = null,
            [nameof(GamesController.GetSystems)] = null,
            [nameof(GamesController.GetArtworkCapabilities)] = null,
            [nameof(GamesController.GetArtworkManifest)] = null,
            [nameof(GamesController.PostArtworkPriority)] = null,
            [nameof(GamesController.GetArtwork)] = null,
            [nameof(GamesController.RefreshArtwork)] = "RequiresElevation",
            [nameof(GamesController.GetGames)] = null,
            [nameof(GamesController.GetGame)] = null,
            [nameof(GamesController.PutCoreOverride)] = null,
            [nameof(GamesController.PutBackendOverride)] = null,
            [nameof(GamesController.GetArcadeCompatibilityStatus)] = "RequiresElevation",
            [nameof(GamesController.PutArcadeCompatibilityDat)] = "RequiresElevation",
            [nameof(GamesController.GetRom)] = null,
            [nameof(GamesController.GetThumb)] = null,
            [nameof(GamesController.GetBios)] = null,
            [nameof(GamesController.GetCoresStatus)] = null,
            [nameof(GamesController.InstallCores)] = "RequiresElevation",
            [nameof(GamesController.UploadCores)] = "RequiresElevation",
        };

    public static IEnumerable<object?[]> GamesControllerActionPolicyCases() =>
        ExpectedGamesControllerPolicies.Select(pair => new object?[] { pair.Key, pair.Value });

    [Theory]
    [MemberData(nameof(GamesControllerActionPolicyCases))]
    public void GamesControllerAction_DeclaresTheExpectedAuthorizationPolicy(string actionName, string? expectedPolicy)
    {
        AssertActionHasExactlyOnePolicy(typeof(GamesController), actionName, expectedPolicy);
    }

    /// <summary>
    /// Enumerates every public action GamesController actually exposes via reflection and
    /// asserts each one is a key in <see cref="ExpectedGamesControllerPolicies"/>. A new
    /// endpoint with no matching entry fails this test until its authorization is deliberately
    /// declared above -- it cannot silently inherit "no expectation checked".
    /// </summary>
    [Fact]
    public void GamesController_NoActionIsMissingAnAuthorizationExpectation()
    {
        var actualActions = GetControllerActionNames(typeof(GamesController));

        var undeclared = actualActions.Where(name => !ExpectedGamesControllerPolicies.ContainsKey(name)).ToList();
        Assert.True(undeclared.Count == 0, $"New action(s) with no declared authorization expectation: {string.Join(", ", undeclared)}");

        var stale = ExpectedGamesControllerPolicies.Keys.Where(name => !actualActions.Contains(name)).ToList();
        Assert.True(stale.Count == 0, $"Expectation(s) reference action(s) that no longer exist: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// EmulatorController deliberately serves player.html, moonfin-bridge.js, and the data/
    /// asset folder to anonymous callers (see its class doc: the WebView loads the shell with
    /// no auth header; ROM/BIOS/save URLs it fetches carry their own ApiKey and stay
    /// authorized). This pins that as an intentional, closed set so [AllowAnonymous] cannot
    /// spread to a future action without this test being updated on purpose.
    /// </summary>
    private static readonly IReadOnlySet<string> ExpectedAnonymousEmulatorControllerActions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(EmulatorController.GetPlayer),
            nameof(EmulatorController.GetMoonfinBridge),
            nameof(EmulatorController.GetDataAsset),
        };

    [Theory]
    [InlineData(nameof(EmulatorController.GetPlayer))]
    [InlineData(nameof(EmulatorController.GetMoonfinBridge))]
    [InlineData(nameof(EmulatorController.GetDataAsset))]
    public void EmulatorControllerAction_IsDeliberatelyAnonymous(string actionName)
    {
        var method = typeof(EmulatorController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing {actionName} action.");

        Assert.NotEmpty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        Assert.Empty(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
    }

    /// <summary>
    /// Same closed-set guarantee as <see cref="GamesController_NoActionIsMissingAnAuthorizationExpectation"/>:
    /// a new EmulatorController action must be either added to the anonymous allow-list above
    /// (consciously) or given an [Authorize]; it cannot land uncovered.
    /// </summary>
    [Fact]
    public void EmulatorController_NoActionIsMissingAnAuthorizationExpectation()
    {
        foreach (var actionName in GetControllerActionNames(typeof(EmulatorController)))
        {
            var method = typeof(EmulatorController).GetMethod(actionName)!;
            var isAnonymous = method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Any();
            var isAuthorized = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Any()
                || typeof(EmulatorController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Any();

            if (ExpectedAnonymousEmulatorControllerActions.Contains(actionName))
            {
                Assert.True(isAnonymous, $"{actionName} is expected to be anonymous but has no [AllowAnonymous].");
            }
            else
            {
                Assert.True(isAuthorized && !isAnonymous, $"{actionName} is not in the anonymous allow-list, so it must declare [Authorize] and must not be [AllowAnonymous].");
            }
        }
    }

    private static void AssertActionHasExactlyOnePolicy(Type controllerType, string actionName, string? expectedPolicy)
    {
        var method = controllerType.GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing {actionName} action on {controllerType.Name}.");

        var methodAuthorize = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().ToList();
        var classAuthorize = controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().ToList();

        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));

        var attr = methodAuthorize.FirstOrDefault() ?? classAuthorize.FirstOrDefault();
        Assert.True(attr != null, $"{actionName} has no [Authorize] attribute (method or controller level).");
        Assert.Equal(expectedPolicy, attr!.Policy);
    }

    /// <summary>
    /// Public, non-special-name instance methods declared directly on <paramref name="controllerType"/>
    /// that carry an HTTP verb attribute (HttpGet/HttpPost/etc.) -- i.e. the controller's action
    /// methods, excluding private helpers and anything inherited from ControllerBase.
    /// </summary>
    private static HashSet<string> GetControllerActionNames(Type controllerType) =>
        controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true).Any())
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
}
