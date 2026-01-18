using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ConX.Infrastructure.Logging;

public sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();
    private bool _disposed;

    public SimpleFileLoggerProvider(string folder, LogLevel minLevel = LogLevel.Information)
    {
        _folder = folder;
        _minLevel = minLevel;
        Directory.CreateDirectory(_folder);
    }

    public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(_folder, categoryName, _minLevel, _lock);

    public void Dispose() => _disposed = true;

    private sealed class SimpleFileLogger : ILogger
    {
        private readonly string _folder;
        private readonly string _category;
        private readonly LogLevel _minLevel;
        private readonly object _lock;

        public SimpleFileLogger(string folder, string category, LogLevel minLevel, object sharedLock)
        {
            _folder = folder;
            _category = category;
            _minLevel = minLevel;
            _lock = sharedLock;
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            var now = DateTimeOffset.Now;
            var fileName = Path.Combine(_folder, $"log-{now:yyyyMMdd}.txt");
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}][{logLevel}]|{_category}|{message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }
            lock (_lock)
            {
                File.AppendAllText(fileName, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
