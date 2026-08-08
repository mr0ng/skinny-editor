using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Core;

/// <summary>
/// Keeps one debounced, local recovery snapshot for an editor scene. Recovery data is
/// deliberately stored outside the project so it never becomes source-controlled content.
/// </summary>
public sealed class SceneRecoveryStore : IDisposable
{
    private const int CurrentEnvelopeVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly Guid _projectId;
    private readonly string _scenePath;
    private readonly Timer _timer;
    private RecoveryEnvelope? _latest;
    private bool _disposed;

    public SceneRecoveryStore(
        Guid projectId,
        string scenePath,
        string? recoveryRoot = null,
        TimeSpan? debounce = null)
    {
        _projectId = projectId;
        _scenePath = Path.GetFullPath(scenePath);
        var root = Path.GetFullPath(recoveryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SKinnyEditor",
            "Recovery"));
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{projectId:N}:{_scenePath.ToUpperInvariant()}"));
        var key = Convert.ToHexString(keyBytes).ToLowerInvariant()[..24];
        RecoveryPath = Path.Combine(root, projectId.ToString("N"), $"{key}.recovery.json");
        Debounce = debounce ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(HandleTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string RecoveryPath { get; }
    public TimeSpan Debounce { get; }

    public event EventHandler<Exception>? WriteFailed;

    public SceneRecoveryRecord? TryLoad()
    {
        try
        {
            if (!File.Exists(RecoveryPath))
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<RecoveryEnvelope>(
                File.ReadAllText(RecoveryPath),
                Options);
            if (envelope is null
                || envelope.EnvelopeVersion != CurrentEnvelopeVersion
                || envelope.ProjectId != _projectId
                || !Path.GetFullPath(envelope.ScenePath).Equals(_scenePath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var sourceWriteTicks = File.Exists(_scenePath)
                ? File.GetLastWriteTimeUtc(_scenePath).Ticks
                : 0;
            return new SceneRecoveryRecord(
                SceneSerializer.Deserialize(envelope.SceneJson),
                envelope.Revision,
                envelope.CapturedUtc,
                sourceWriteTicks != envelope.SourceWriteUtcTicks);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException)
        {
            return null;
        }
    }

    public void Schedule(SceneDocument document, long revision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var envelope = new RecoveryEnvelope(
            CurrentEnvelopeVersion,
            _projectId,
            _scenePath,
            DateTimeOffset.UtcNow,
            revision,
            File.Exists(_scenePath) ? File.GetLastWriteTimeUtc(_scenePath).Ticks : 0,
            SceneSerializer.Serialize(document));
        lock (_gate)
        {
            _latest = envelope;
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? failure;
        lock (_gate)
        {
            failure = WriteLatestLocked();
        }

        if (failure is not null)
        {
            WriteFailed?.Invoke(this, failure);
        }
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _latest = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                if (File.Exists(RecoveryPath))
                {
                    File.Delete(RecoveryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WriteFailed?.Invoke(this, exception);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Exception? failure;
        lock (_gate)
        {
            failure = WriteLatestLocked();
            _disposed = true;
        }

        _timer.Dispose();
        if (failure is not null)
        {
            WriteFailed?.Invoke(this, failure);
        }
    }

    private void HandleTimer(object? state)
    {
        Exception? failure;
        lock (_gate)
        {
            failure = WriteLatestLocked();
        }

        if (failure is not null)
        {
            WriteFailed?.Invoke(this, failure);
        }
    }

    private Exception? WriteLatestLocked()
    {
        if (_latest is null)
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(RecoveryPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{RecoveryPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_latest, Options));
                File.Move(temporaryPath, RecoveryPath, overwrite: true);
                _latest = null;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    private sealed record RecoveryEnvelope(
        int EnvelopeVersion,
        Guid ProjectId,
        string ScenePath,
        DateTimeOffset CapturedUtc,
        long Revision,
        long SourceWriteUtcTicks,
        string SceneJson);
}

public sealed record SceneRecoveryRecord(
    SceneDocument Document,
    long Revision,
    DateTimeOffset CapturedUtc,
    bool SourceChangedSinceCapture);
