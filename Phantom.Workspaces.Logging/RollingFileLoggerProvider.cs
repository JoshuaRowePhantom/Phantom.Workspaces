using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// A small custom <see cref="ILoggerProvider"/> that writes to dated rolling files
/// (<c>phantom-workspaces-yyyyMMdd.log</c>) in a supplied log directory, enforcing a retention
/// period (7 days by default). The directory is supplied by the caller (the config-driven
/// <c>ILogDirectoryProvider</c> for the main process, or <c>HostLogDirectoryResolver</c> for
/// config-less hosts); this provider never resolves a directory itself. Old files are pruned on
/// construction and whenever the active dated file rolls over to a new day.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const string FilePrefix = "phantom-workspaces-";
    private const string FileExtension = ".log";

    private readonly string logDirectory;
    private readonly TimeSpan retention;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    private StreamWriter? writer;
    private string? currentDateStamp;
    private bool disposed;

    public RollingFileLoggerProvider(string logDirectory, TimeSpan retention)
        : this(logDirectory, retention, TimeProvider.System)
    {
    }

    internal RollingFileLoggerProvider(string logDirectory, TimeSpan retention, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.logDirectory = logDirectory;
        this.retention = retention;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        Directory.CreateDirectory(this.logDirectory);
        this.PruneOldFiles();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    internal void Write(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            var now = this.timeProvider.GetUtcNow().UtcDateTime;
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            this.EnsureWriter(dateStamp);

            var builder = new StringBuilder();
            builder.Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            builder.Append(" [").Append(logLevel).Append("] ");
            builder.Append(categoryName);
            builder.Append(" - ");
            builder.Append(message);
            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            this.writer!.WriteLine(builder.ToString());
            this.writer.Flush();
        }
    }

    private void EnsureWriter(string dateStamp)
    {
        if (this.writer is not null && string.Equals(this.currentDateStamp, dateStamp, StringComparison.Ordinal))
        {
            return;
        }

        // A new day (or first write): roll to the new dated file and prune expired files.
        this.writer?.Flush();
        this.writer?.Dispose();

        var path = Path.Combine(this.logDirectory, $"{FilePrefix}{dateStamp}{FileExtension}");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        this.writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        this.currentDateStamp = dateStamp;

        this.PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        var cutoff = this.timeProvider.GetUtcNow().UtcDateTime - this.retention;

        if (!Directory.Exists(this.logDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(this.logDirectory, $"{FilePrefix}*{FileExtension}"))
        {
            if (GetFileTimestampUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // File is in use; it will be retried on the next sweep.
                }
                catch (UnauthorizedAccessException)
                {
                    // Access denied; skip and retry on the next sweep.
                }
            }
        }
    }

    // The age of a log file is taken from the date embedded in its name
    // (phantom-workspaces-yyyyMMdd.log); if that cannot be parsed the last-write time is used.
    private static DateTime GetFileTimestampUtc(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            var stamp = name[FilePrefix.Length..];
            if (DateTime.TryParseExact(
                stamp,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                // Treat the file as belonging to the end of its dated day so a file dated "today"
                // is never considered expired by a same-day cutoff.
                return parsed.Date.AddDays(1).AddTicks(-1);
            }
        }

        return File.GetLastWriteTimeUtc(filePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.writer?.Flush();
            this.writer?.Dispose();
            this.writer = null;
        }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!this.IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            provider.Write(categoryName, logLevel, message, exception);
        }
    }
}
