namespace StereoKitEditor.App.Services;

public sealed class SceneCrashRecoveryPolicy(
    int maximumCrashes = 2,
    TimeSpan? window = null)
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _crashesByBuild = new(StringComparer.Ordinal);
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(30);

    public bool ShouldRestart(string buildId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(buildId))
        {
            return false;
        }

        if (!_crashesByBuild.TryGetValue(buildId, out var crashes))
        {
            crashes = new();
            _crashesByBuild.Add(buildId, crashes);
        }

        while (crashes.TryPeek(out var crash) && now - crash > _window)
        {
            crashes.Dequeue();
        }

        crashes.Enqueue(now);
        return crashes.Count < maximumCrashes;
    }
}
