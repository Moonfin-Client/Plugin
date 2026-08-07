using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

public class EmulatorResourcesTests
{
    private static readonly Assembly ServerAssembly = typeof(Moonfin.Server.Api.EmulatorController).Assembly;

    [Fact]
    public void PlayerShell_LoadsEmbeddedBridgeBeforeTheEmulatorLoader()
    {
        var player = ReadResource("Moonfin.Server.EmulatorJS.player.html");

        Assert.Contains("<script src=\"moonfin-bridge.js\"></script>", player, StringComparison.Ordinal);
        Assert.True(
            player.IndexOf("moonfin-bridge.js", StringComparison.Ordinal) < player.IndexOf("loader.js", StringComparison.Ordinal));
    }

    [Fact]
    public void MoonfinBridge_IsEmbeddedWithTheNativeHostEntryPoints()
    {
        var bridge = ReadResource("Moonfin.Server.EmulatorJS.moonfin-bridge.js");

        foreach (var entryPoint in new[]
        {
            "moonfinRegisterGamepads", "moonfinGamepadInput", "moonfinKeyboardInput", "moonfinControlInput",
            "moonfinGetState", "moonfinLoadState", "moonfinRestart", "moonfinFastForward", "moonfinPause",
            "moonfinGetSettings", "moonfinGetOptions", "moonfinSetOption", "moonfinOpenControls", "moonfinControlsOpen"
        })
        {
            Assert.Contains(entryPoint, bridge, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MoonfinBridge_UsesTheAnonymousEmbeddedAssetEndpoint()
    {
        var method = typeof(EmulatorController).GetMethod(nameof(EmulatorController.GetMoonfinBridge));
        Assert.NotNull(method);
        var route = Assert.IsType<HttpGetAttribute>(method!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("moonfin-bridge.js", route.Template);
        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());

        var result = new EmulatorController().GetMoonfinBridge();
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/javascript", content.ContentType);
        Assert.Contains("moonfinRegisterGamepads", content.Content, StringComparison.Ordinal);
    }

    private static string ReadResource(string name)
    {
        using var stream = ServerAssembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded resource '{name}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
