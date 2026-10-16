using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Meridian.Tests.Integration;

// Keeps every log line (with its scopes, like the console shows them) so tests can assert on logs
public class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<string> Lines { get; } = new();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private class Logger : ILogger
    {
        private readonly LogCapture _owner;
        public Logger(LogCapture owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = new StringBuilder();
            _owner._scopes.ForEachScope((scope, sb) => sb.Append(scope).Append(" => "), line);
            line.Append(formatter(state, exception));
            _owner.Lines.Enqueue(line.ToString());
        }
    }
}
