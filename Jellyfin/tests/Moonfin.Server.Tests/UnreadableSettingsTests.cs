using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class UnreadableSettingsTests
{
    [Fact]
    public void RegisteredFilterMapsOnlyUnreadableSettingsTo503()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();
        var filter = Assert.Single(provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters.OfType<UnreadableSettingsFilter>());
        var action = new ActionContext(new DefaultHttpContext(), new(), new());
        var io = new IOException("unavailable");
        var context = new ExceptionContext(action, []) { Exception = io };
        filter.OnException(context);
        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);

        context.Exception = new UnreadableSettingsException(io);
        filter.OnException(context);
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(context.Exception.Message, JsonSerializer.SerializeToElement(result.Value).GetProperty("Error").GetString());
    }
}
