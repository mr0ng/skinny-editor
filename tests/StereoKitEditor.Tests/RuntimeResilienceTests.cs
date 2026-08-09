using StereoKitEditor.Protocol;

namespace StereoKitEditor.Tests;

public sealed class RuntimeResilienceTests
{
    [Fact]
    public void LogBuffer_CoalescesARepeatedRenderFailureIntoOneMessage()
    {
        var buffer = new BoundedRuntimeLogBuffer(capacity: 8);
        var flushRequests = 0;

        Parallel.For(0, 10_000, _ =>
        {
            if (buffer.Enqueue("Error", "vkQueuePresentKHR failed: 0xFFFFFFFC"))
            {
                Interlocked.Increment(ref flushRequests);
            }
        });

        var message = Assert.Single(buffer.Drain());
        Assert.Equal(1, flushRequests);
        Assert.Equal("Error", message.Level);
        Assert.Equal(
            "vkQueuePresentKHR failed: 0xFFFFFFFC (repeated 10,000 times)",
            message.Text);
        Assert.Empty(buffer.Drain());
    }

    [Fact]
    public void LogBuffer_BoundsDistinctMessagesAndReportsSuppression()
    {
        var buffer = new BoundedRuntimeLogBuffer(capacity: 3);

        for (var index = 0; index < 1_000; index++)
        {
            buffer.Enqueue("Info", $"Unique message {index}");
        }

        var messages = buffer.Drain();
        Assert.Equal(4, messages.Count);
        Assert.Equal(
            ["Unique message 0", "Unique message 1", "Unique message 2"],
            messages.Take(3).Select(message => message.Text));
        Assert.Equal("Warning", messages[3].Level);
        Assert.Equal(
            "Suppressed 997 additional runtime log messages to protect editor responsiveness.",
            messages[3].Text);
    }

    [Fact]
    public void LogBuffer_RequestsAnotherFlushAfterBeingDrained()
    {
        var buffer = new BoundedRuntimeLogBuffer();

        Assert.True(buffer.Enqueue("Info", "first"));
        Assert.False(buffer.Enqueue("Info", "second"));
        Assert.Equal(2, buffer.Drain().Count);
        Assert.True(buffer.Enqueue("Info", "third"));
    }

    [Fact]
    public void LogBuffer_TruncatesAnIndividualOversizedMessage()
    {
        var buffer = new BoundedRuntimeLogBuffer(capacity: 4, maximumTextLength: 32);

        buffer.Enqueue("Info", new string('x', 10_000));

        var message = Assert.Single(buffer.Drain());
        Assert.Equal(32, message.Text.Length);
        Assert.EndsWith("… [truncated]", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[sk_renderer] vkQueuePresentKHR failed: 0xFFFFFFFC")]
    [InlineData("Vulkan returned VK_ERROR_DEVICE_LOST")]
    [InlineData("vulkan returned vk_error_device_lost")]
    [InlineData("Present failed with DXGI_ERROR_DEVICE_REMOVED")]
    [InlineData("Present failed with DXGI_ERROR_DEVICE_HUNG")]
    [InlineData("Present failed with DXGI_ERROR_DEVICE_RESET")]
    public void FailureClassifier_RecognizesGraphicsDeviceLoss(string text)
    {
        Assert.True(RuntimeFailureClassifier.TryClassifyFatalLog("Error", text, out var message));
        Assert.Equal("StereoKit lost the graphics device while presenting a frame.", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vkQueuePresentKHR failed: 0xFFFFFFFD")]
    [InlineData("VK_ERROR_SURFACE_LOST_KHR")]
    [InlineData("Ordinary project warning")]
    public void FailureClassifier_DoesNotEscalateRecoverableOrUnrelatedLogs(string text)
    {
        Assert.False(RuntimeFailureClassifier.TryClassifyFatalLog("Error", text, out var message));
        Assert.Empty(message);
    }

    [Fact]
    public void FailureClassifier_DoesNotEscalateADeviceLossStringAtNonErrorSeverity()
    {
        Assert.False(RuntimeFailureClassifier.TryClassifyFatalLog(
            "Info",
            "Vulkan returned VK_ERROR_DEVICE_LOST",
            out var message));
        Assert.Empty(message);
    }

    [Fact]
    public void LogBuffer_RejectsAnUnboundedConfiguration() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedRuntimeLogBuffer(0));
}
