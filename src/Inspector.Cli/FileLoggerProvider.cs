
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter writer;
    private readonly object sync = new();
    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { lock (sync) writer.Dispose(); }
    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var entry = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, level = logLevel.ToString(),
                category, message = formatter(state, exception), exception = exception?.ToString() });
            lock (provider.sync) provider.writer.WriteLine(entry);
        }
    }
}


