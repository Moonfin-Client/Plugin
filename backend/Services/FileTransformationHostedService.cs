using System.Reflection;
using System.Runtime.Loader;
using Moonfin.Server.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Moonfin.Server.Services;

/// <summary>
/// Hosted service that automatically registers Moonfin's file transformations
/// when the plugin is loaded. Unlike the scheduled task variant, this runs
/// automatically on every plugin load — including after plugin updates —
/// without requiring a full Jellyfin restart or manual task execution.
/// </summary>
public class FileTransformationHostedService : IHostedService
{
    private readonly ILogger<FileTransformationHostedService> _logger;

    public FileTransformationHostedService(ILogger<FileTransformationHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Moonfin: Auto-registering file transformations.");

        try
        {
            RegisterTransformation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moonfin: Failed to auto-register file transformations.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RegisterTransformation()
    {
        var payload = new JObject
        {
            { "id", "8c5d0e91-0001-4b6d-9e3f-1a7c8d9e0f2b" },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", GetType().Assembly.FullName },
            { "callbackClass", typeof(TransformationPatches).FullName },
            { "callbackMethod", nameof(TransformationPatches.IndexHtml) }
        };

        Assembly? ftAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);

        if (ftAssembly == null)
        {
            _logger.LogWarning(
                "Moonfin: File Transformation plugin not found. Frontend auto-injection is disabled. " +
                "Install from https://github.com/IAmParadox27/jellyfin-plugin-file-transformation");
            return;
        }

        Type? pluginInterfaceType = ftAssembly
            .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

        if (pluginInterfaceType == null)
        {
            _logger.LogWarning(
                "Moonfin: File Transformation PluginInterface type not available. " +
                "Ensure File Transformation plugin is v2.2.1.0 or later.");
            return;
        }

        pluginInterfaceType.GetMethod("RegisterTransformation")
            ?.Invoke(null, new object?[] { payload });

        _logger.LogInformation("Moonfin: Successfully registered index.html transformation.");
    }
}
