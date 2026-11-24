using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace CdBurner.Core.Persistence;

/// <summary>
/// Diagnostic severity levels for optical burn and rip operations.
/// </summary>
public enum BurnLogLevel
{
    Info,
    Warn,
    Error,
    Debug
}

/// <summary>
/// Structured diagnostic log event recording optical session telemetry, SCSI diagnostics, and burn status.
/// </summary>
public sealed record BurnLogEntry
{
    /// <summary>
    /// Event timestamp in UTC.
    /// </summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Log severity level.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BurnLogLevel Level { get; init; } = BurnLogLevel.Info;

    /// <summary>
    /// Primary diagnostic message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Functional subsystem category (e.g. "BurnEngine", "ScsiPassThrough", "Verification", "Ripper").
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Target Logical Block Address (LBA) associated with this event, if applicable.
    /// </summary>
    public int? Lba { get; init; }

    /// <summary>
    /// Optional exception details, stack trace, or error message.
    /// </summary>
    public string? ExceptionDetail { get; init; }

    /// <summary>
    /// Arbitrary key-value metadata dictionary for structured telemetry.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    /// <summary>
    /// Standard uppercase level identifier string ("INFO", "WARN", "ERROR", "DEBUG").
    /// </summary>
    public string LevelString => Level switch
    {
        BurnLogLevel.Info => "INFO",
        BurnLogLevel.Warn => "WARN",
        BurnLogLevel.Error => "ERROR",
        BurnLogLevel.Debug => "DEBUG",
        _ => Level.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// Formats this entry into standard human-readable diagnostic text:
    /// [yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [Category] [LBA: 12345] Message
    /// </summary>
    public string ToFormattedText()
    {
        var sb = new StringBuilder();
        sb.Append($"[{TimestampUtc:yyyy-MM-dd HH:mm:ss.fff}] [{LevelString}]");

        if (!string.IsNullOrEmpty(Category))
        {
            string safeCategory = Category.Replace("\r", " ").Replace("\n", " ");
            sb.Append($" [{safeCategory}]");
        }

        if (Lba.HasValue)
        {
            sb.Append($" [LBA: {Lba.Value}]");
        }

        string safeMessage = (Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        sb.Append(' ').Append(safeMessage);

        if (!string.IsNullOrEmpty(ExceptionDetail))
        {
            sb.AppendLine().Append("  Exception: ").Append(ExceptionDetail);
        }

        return sb.ToString();
    }

    public override string ToString() => ToFormattedText();
}

/// <summary>
/// Thread-safe structured diagnostic logger maintaining an in-memory fixed-capacity ring buffer,
/// optional real-time JSONL file streaming, and export to human-readable .log text files.
/// </summary>
public sealed class StructuredBurnLogger : IDisposable, IAsyncDisposable
{
    private const int MaxPendingFileEntries = 16_384;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonlSerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _syncLock = new();
    private readonly int _capacity;
    private readonly BurnLogEntry?[] _ringBuffer;
    private int _head;
    private int _count;

    private readonly string? _jsonlFilePath;
    private StreamWriter? _jsonlWriter;
    private Channel<string>? _jsonlChannel;
    private Task? _jsonlWriterTask;
    private Exception? _lastWriteException;
    private bool _isDisposed;

    /// <summary>
    /// Most recent error encountered while serializing or flushing JSONL log output, if any.
    /// </summary>
    public Exception? LastWriteException => Volatile.Read(ref _lastWriteException);

    /// <summary>
    /// Event triggered whenever a new log entry is recorded.
    /// Invocations occur outside the internal logger lock.
    /// </summary>
    public event Action<BurnLogEntry>? EntryLogged;

    /// <summary>
    /// Capacity of the circular ring buffer.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Current number of entries retained in the ring buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_syncLock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Destination JSONL file path, if file logging is active.
    /// </summary>
    public string? JsonlFilePath => _jsonlFilePath;

    /// <summary>
    /// Initializes a new structured burn logger.
    /// </summary>
    /// <param name="capacity">Maximum entries to hold in memory before overwriting oldest entries (default 1000).</param>
    /// <param name="jsonlFilePath">Optional destination file path for streaming JSONL log entries.</param>
    public StructuredBurnLogger(int capacity = 1000, string? jsonlFilePath = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        _capacity = capacity;
        _ringBuffer = new BurnLogEntry?[capacity];
        _head = 0;
        _count = 0;
        _jsonlFilePath = jsonlFilePath;

        if (!string.IsNullOrWhiteSpace(jsonlFilePath))
        {
            string? dir = Path.GetDirectoryName(jsonlFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(jsonlFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            try
            {
                _jsonlWriter = new StreamWriter(stream, Utf8NoBom);
                _jsonlChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingFileEntries)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
                _jsonlWriterTask = Task.Run(() => DrainJsonlAsync(_jsonlWriter, _jsonlChannel.Reader));
            }
            catch
            {
                _jsonlWriter?.Dispose();
                _jsonlWriter = null;
                stream.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Records a structured diagnostic entry thread-safely into the ring buffer and JSONL stream.
    /// </summary>
    public void Log(BurnLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string? jsonLine = null;
        if (_jsonlChannel != null)
        {
            TrySerializeJsonl(entry, out jsonLine);
        }

        Action<BurnLogEntry>? callback;

        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            // Insert into circular ring buffer
            _ringBuffer[_head] = entry;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }

            if (_jsonlChannel != null && jsonLine != null)
            {
                _jsonlChannel.Writer.TryWrite(jsonLine);
            }

            callback = EntryLogged;
        }

        callback?.Invoke(entry);
    }

    private bool TrySerializeJsonl(BurnLogEntry entry, out string? jsonLine)
    {
        try
        {
            jsonLine = JsonSerializer.Serialize(entry, JsonlSerializerOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Volatile.Write(ref _lastWriteException, ex);
            Trace.TraceWarning($"StructuredBurnLogger could not serialize log entry: {ex.Message}");
            jsonLine = null;
            return false;
        }
    }

    private async Task DrainJsonlAsync(StreamWriter writer, ChannelReader<string> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out string? line))
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Volatile.Write(ref _lastWriteException, ex);
            Trace.TraceWarning($"StructuredBurnLogger JSONL write failed for '{_jsonlFilePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public void LogInfo(string message, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Info,
            Message = message,
            Category = category,
            Lba = lba,
            Data = data
        });
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public void LogWarn(string message, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Warn,
            Message = message,
            Category = category,
            Lba = lba,
            Data = data
        });
    }

    /// <summary>
    /// Logs an error message and optional exception.
    /// </summary>
    public void LogError(string message, Exception? ex = null, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Error,
            Message = message,
            Category = category,
            Lba = lba,
            ExceptionDetail = ex?.ToString(),
            Data = data
        });
    }

    /// <summary>
    /// Logs a debug diagnostic message.
    /// </summary>
    public void LogDebug(string message, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Debug,
            Message = message,
            Category = category,
            Lba = lba,
            Data = data
        });
    }

    /// <summary>
    /// Returns all entries currently stored in the ring buffer in chronological order (oldest to newest).
    /// </summary>
    public IReadOnlyList<BurnLogEntry> GetAllEntries()
    {
        lock (_syncLock)
        {
            var result = new List<BurnLogEntry>(_count);
            int start = (_count < _capacity) ? 0 : _head;

            for (int i = 0; i < _count; i++)
            {
                int index = (start + i) % _capacity;
                var item = _ringBuffer[index];
                if (item != null)
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Exports all entries currently in the ring buffer to a standard human-readable text .log file.
    /// </summary>
    public void ExportToLogFile(string destinationFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var entries = GetAllEntries();

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var writer = new StreamWriter(destinationFilePath, append: false, Utf8NoBom);
        foreach (var entry in entries)
        {
            writer.WriteLine(entry.ToFormattedText());
        }
    }

    /// <summary>
    /// Asynchronously exports all entries currently in the ring buffer to a standard human-readable text .log file.
    /// </summary>
    public async Task ExportToLogFileAsync(string destinationFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var entries = GetAllEntries();

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var writer = new StreamWriter(destinationFilePath, append: false, Utf8NoBom);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(entry.ToFormattedText()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears all entries from the in-memory ring buffer.
    /// </summary>
    public void Clear()
    {
        lock (_syncLock)
        {
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
            _head = 0;
            _count = 0;
        }
    }

    public void Dispose()
    {
        Channel<string>? channel;
        Task? writerTask;
        StreamWriter? writer;

        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            channel = _jsonlChannel;
            writerTask = _jsonlWriterTask;
            writer = _jsonlWriter;
            _jsonlChannel = null;
            _jsonlWriterTask = null;
            _jsonlWriter = null;
        }

        channel?.Writer.TryComplete();
        if (writerTask != null)
        {
            writerTask.GetAwaiter().GetResult();
        }

        if (writer != null)
        {
            try
            {
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Volatile.Write(ref _lastWriteException, ex);
            }
            writer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Channel<string>? channel;
        Task? writerTask;
        StreamWriter? writer;

        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            channel = _jsonlChannel;
            writerTask = _jsonlWriterTask;
            writer = _jsonlWriter;
            _jsonlChannel = null;
            _jsonlWriterTask = null;
            _jsonlWriter = null;
        }

        channel?.Writer.TryComplete();
        if (writerTask != null)
        {
            await writerTask.ConfigureAwait(false);
        }

        if (writer != null)
        {
            try
            {
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Volatile.Write(ref _lastWriteException, ex);
            }
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
