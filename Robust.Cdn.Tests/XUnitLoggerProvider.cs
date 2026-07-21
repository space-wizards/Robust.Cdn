using Xunit.Abstractions;

namespace Robust.Cdn.Tests;

public sealed class XUnitLoggerProvider(ITestOutputHelper testOutput) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new XUnitLogger(categoryName, testOutput);

    public void Dispose()
    {
    }

    private sealed class XUnitLogger(string categoryName, ITestOutputHelper testOutput) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            try
            {
                testOutput.WriteLine($"[{logLevel}] [{categoryName}] {message}");
                if (exception != null)
                    testOutput.WriteLine($"  Exception: {exception}");
            }
            catch { /* ignore */ }
        }
    }
}
