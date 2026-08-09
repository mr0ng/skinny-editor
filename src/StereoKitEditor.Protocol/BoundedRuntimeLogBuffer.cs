using System.Globalization;

namespace StereoKitEditor.Protocol;

/// <summary>
/// Coalesces repeated runtime logs while keeping the number of distinct pending
/// messages bounded. Producers never wait for the consumer, so a failing render
/// loop cannot create an unbounded task or UI-dispatch backlog.
/// </summary>
public sealed class BoundedRuntimeLogBuffer(int capacity = 64, int maximumTextLength = 16 * 1024)
{
    private const string TruncationSuffix = "… [truncated]";
    private readonly object _gate = new();
    private readonly Dictionary<LogKey, long> _counts = [];
    private readonly List<LogKey> _order = [];
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), "Log buffer capacity must be positive.");
    private readonly int _maximumTextLength = maximumTextLength > TruncationSuffix.Length
        ? maximumTextLength
        : throw new ArgumentOutOfRangeException(
            nameof(maximumTextLength),
            $"Maximum log text length must be greater than {TruncationSuffix.Length} characters.");
    private long _suppressedMessages;

    /// <summary>
    /// Adds a message and returns true when the buffer transitioned from empty
    /// to non-empty, allowing consumers to schedule at most one pending flush.
    /// </summary>
    public bool Enqueue(string level, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        level = string.IsNullOrWhiteSpace(level) ? "Info" : level;
        if (text.Length > _maximumTextLength)
        {
            text = $"{text[..(_maximumTextLength - TruncationSuffix.Length)]}{TruncationSuffix}";
        }

        var key = new LogKey(level, text);
        lock (_gate)
        {
            var wasEmpty = _order.Count == 0 && _suppressedMessages == 0;
            if (_counts.TryGetValue(key, out var count))
            {
                _counts[key] = count == long.MaxValue ? count : count + 1;
            }
            else if (_order.Count < _capacity)
            {
                _counts.Add(key, 1);
                _order.Add(key);
            }
            else if (_suppressedMessages < long.MaxValue)
            {
                _suppressedMessages++;
            }

            return wasEmpty;
        }
    }

    public IReadOnlyList<RuntimeLogMessage> Drain()
    {
        lock (_gate)
        {
            if (_order.Count == 0 && _suppressedMessages == 0)
            {
                return [];
            }

            var messages = new List<RuntimeLogMessage>(
                _order.Count + (_suppressedMessages > 0 ? 1 : 0));
            foreach (var key in _order)
            {
                var count = _counts[key];
                messages.Add(new(
                    key.Level,
                    count == 1
                        ? key.Text
                        : $"{key.Text} (repeated {count.ToString("N0", CultureInfo.InvariantCulture)} times)"));
            }

            if (_suppressedMessages > 0)
            {
                messages.Add(new(
                    "Warning",
                    $"Suppressed {_suppressedMessages.ToString("N0", CultureInfo.InvariantCulture)} additional runtime log messages to protect editor responsiveness."));
            }

            _counts.Clear();
            _order.Clear();
            _suppressedMessages = 0;
            return messages;
        }
    }

    private sealed record LogKey(string Level, string Text);
}
