using StereoKitEditor.App.Services;

namespace StereoKitEditor.Tests;

public sealed class SceneCrashRecoveryPolicyTests
{
    [Fact]
    public void Recovery_RestartsOnceThenSuppressesSameBuildCrashLoop()
    {
        var policy = new SceneCrashRecoveryPolicy();
        var now = DateTimeOffset.UtcNow;

        Assert.True(policy.ShouldRestart("build-a", now));
        Assert.False(policy.ShouldRestart("build-a", now.AddSeconds(1)));
        Assert.True(policy.ShouldRestart("build-b", now.AddSeconds(2)));
        Assert.True(policy.ShouldRestart("build-a", now.AddSeconds(32)));
    }
}
