using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Tests;

/// <summary>
/// Test doubles with no per-test behaviour, shared so a change to one does not have to be
/// repeated across files. Fakes that a test actually programs -- the HTTP message handlers, which
/// differ in what they record and return -- deliberately stay with the tests that own them.
/// </summary>
internal sealed class NoOpLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}

/// <summary>Hands every caller the same message handler, whatever the client name.</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler);
}

/// <summary>
/// For a collaborator whose download path a test is asserting is never reached. Throwing rather
/// than returning a dead client turns an unexpected request into an immediate, named failure
/// instead of a confusing timeout or empty response somewhere further along.
/// </summary>
internal sealed class ThrowingHttpClientFactory : IHttpClientFactory
{
    private readonly string _reason;

    public ThrowingHttpClientFactory(string reason = "This test never exercises the download path.")
    {
        _reason = reason;
    }

    public HttpClient CreateClient(string name) => throw new NotSupportedException(_reason);
}
