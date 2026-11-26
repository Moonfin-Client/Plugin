using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

internal sealed class UnreadableSettingsFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is UnreadableSettingsException error)
        {
            context.Result = new ObjectResult(new { Error = error.Message })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            context.ExceptionHandled = true;
        }
    }
}
