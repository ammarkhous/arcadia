using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Logging;

/// <summary>
/// Minimal rolling-by-day file logger. Writes happen on a background task so logging never blocks the UI thread.
/// Bounded: files older than <see cref="RetentionDays"/> days are deleted at startup, the folder is kept under
/// <see cref="MaxTotalBytes"/> (oldest first), and one day's file stops growing at <see cref="MaxFileBytes"/>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private bool _capped;

    public const int RetentionDays = 14;
    public const long MaxTotalBytes = 50L * 1024 * 1024;
    public const long MaxFileBytes = 20L * 1024 * 1024;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        Prune(directory, DateTime.Now);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Deletes old logs: past the retention period, then the oldest until the folder fits the size limit.</summary>
    public static int Prune(string directory, DateTime now)
    {
        var deleted = 0;
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("arcadia-*.log").OrderBy(f => f.LastWriteTime).ToList();
            foreach (var f in files.Where(f => now - f.LastWriteTime > TimeSpan.FromDays(RetentionDays)).ToList())
            {
                f.Delete();
                files.Remove(f);
                deleted++;
            }
            var total = files.Sum(f => f.Length);
            foreach (var f in files.ToList())
            {
                if (total <= MaxTotalBytes || files.Count <= 1) break;
                total -= f.Length;
                f.Delete();
                files.Remove(f);
                deleted++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return deleted;
    }

    public string CurrentLogFile => Path.Combine(_directory, $"arcadia-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var file = CurrentLogFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    if (!_capped) await File.AppendAllTextAsync(file, "… log size limit reached for today; further lines are dropped" + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
                    _capped = true;
                    continue;
                }
                _capped = false;
                await File.AppendAllTextAsync(file, line, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Logging must never crash the app; the Debug provider still receives the message.
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
                .Append(category).Append(": ")
                .AppendLine(formatter(state, exception));
            if (exception != null) sb.AppendLine(exception.ToString());
            provider.Enqueue(sb.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
