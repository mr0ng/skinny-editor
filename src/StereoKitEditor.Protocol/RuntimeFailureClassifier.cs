namespace StereoKitEditor.Protocol;

public static class RuntimeFailureClassifier
{
    public static bool TryClassifyFatalLog(string level, string text, out string message)
    {
        if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(text)
            && (text.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
                || (text.Contains("vkQueuePresentKHR", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("0xFFFFFFFC", StringComparison.OrdinalIgnoreCase))
                || text.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
                || text.Contains("DXGI_ERROR_DEVICE_HUNG", StringComparison.OrdinalIgnoreCase)
                || text.Contains("DXGI_ERROR_DEVICE_RESET", StringComparison.OrdinalIgnoreCase)))
        {
            message = "StereoKit lost the graphics device while presenting a frame.";
            return true;
        }

        message = string.Empty;
        return false;
    }
}
