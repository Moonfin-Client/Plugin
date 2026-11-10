using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

// The shared games code is written against Microsoft.Extensions.Logging and IHttpClientFactory,
// which Emby's SDK doesn't have, so this file declares the few members that code calls and
// forwards them to Emby's own logger and a shared HttpClient handler.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}

namespace Microsoft.Extensions.Logging
{
    public enum LogLevel
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical,
        None
    }

    public interface ILogger
    {
        void Log(LogLevel level, Exception? exception, string message, object?[] args);
    }

    public interface ILogger<out TCategoryName> : ILogger
    {
    }

    public static class LoggerExtensions
    {
        public static void LogDebug(this ILogger logger, string message, params object?[] args) =>
            logger.Log(LogLevel.Debug, null, message, args);

        public static void LogDebug(this ILogger logger, Exception? exception, string message, params object?[] args) =>
            logger.Log(LogLevel.Debug, exception, message, args);

        public static void LogInformation(this ILogger logger, string message, params object?[] args) =>
            logger.Log(LogLevel.Information, null, message, args);

        public static void LogInformation(this ILogger logger, Exception? exception, string message, params object?[] args) =>
            logger.Log(LogLevel.Information, exception, message, args);

        public static void LogWarning(this ILogger logger, string message, params object?[] args) =>
            logger.Log(LogLevel.Warning, null, message, args);

        public static void LogWarning(this ILogger logger, Exception? exception, string message, params object?[] args) =>
            logger.Log(LogLevel.Warning, exception, message, args);

        public static void LogError(this ILogger logger, string message, params object?[] args) =>
            logger.Log(LogLevel.Error, null, message, args);

        public static void LogError(this ILogger logger, Exception? exception, string message, params object?[] args) =>
            logger.Log(LogLevel.Error, exception, message, args);
    }
}

namespace Microsoft.Extensions.Logging.Abstractions
{
    public sealed class NullLogger<T> : ILogger<T>
    {
        public static readonly NullLogger<T> Instance = new NullLogger<T>();

        public void Log(LogLevel level, Exception? exception, string message, object?[] args)
        {
        }
    }
}

namespace System.Net.Http
{
    public interface IHttpClientFactory
    {
        HttpClient CreateClient(string name);
    }

    public static class HttpClientFactoryExtensions
    {
        public static HttpClient CreateClient(this IHttpClientFactory factory) => factory.CreateClient(string.Empty);
    }
}

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>Writes the shared code's log lines to an Emby logger.</summary>
    internal sealed class EmbyLogger<T> : ILogger<T>
    {
        private readonly MediaBrowser.Model.Logging.ILogger _inner;

        public EmbyLogger(MediaBrowser.Model.Logging.ILogger inner) => _inner = inner;

        public void Log(LogLevel level, Exception? exception, string message, object?[] args)
        {
            var text = Render(message, args);
            switch (level)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    if (exception != null) _inner.ErrorException(text, exception);
                    else _inner.Error(text);
                    break;
                case LogLevel.Warning:
                    _inner.Warn(WithException(text, exception));
                    break;
                case LogLevel.Information:
                    _inner.Info(WithException(text, exception));
                    break;
                case LogLevel.None:
                    break;
                default:
                    _inner.Debug(WithException(text, exception));
                    break;
            }
        }

        private static string WithException(string text, Exception? exception) =>
            exception == null ? text : text + ": " + exception.Message;

        /// <summary>Fills a message template's named holes, in order, with the arguments.</summary>
        internal static string Render(string template, object?[] args)
        {
            var sb = new StringBuilder(template.Length + 32);
            var next = 0;
            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                var close = c == '{' ? template.IndexOf('}', i + 1) : -1;
                if (close < 0)
                {
                    sb.Append(c);
                    continue;
                }

                var hole = template.Substring(i + 1, close - i - 1);
                var colon = hole.IndexOf(':');
                var format = colon >= 0 ? hole.Substring(colon + 1) : null;
                var value = next < args.Length ? args[next] : null;
                next++;
                sb.Append(value is IFormattable formattable
                    ? formattable.ToString(format, CultureInfo.InvariantCulture)
                    : value?.ToString() ?? "(null)");
                i = close;
            }

            return sb.ToString();
        }
    }

    /// <summary>Hands out HttpClients that share one handler, so connections get reused.</summary>
    internal sealed class EmbyHttpClientFactory : IHttpClientFactory
    {
        private static readonly HttpClientHandler Handler = new HttpClientHandler();

        public static readonly EmbyHttpClientFactory Instance = new EmbyHttpClientFactory();

        public HttpClient CreateClient(string name) => new HttpClient(Handler, disposeHandler: false);
    }
}
