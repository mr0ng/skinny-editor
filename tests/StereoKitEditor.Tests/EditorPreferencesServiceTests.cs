using StereoKitEditor.App.Services;

namespace StereoKitEditor.Tests;

public sealed class EditorPreferencesServiceTests
{
    [Fact]
    public void Preferences_RoundTripAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skinny-preferences-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(root, "preferences.json");
            var service = new EditorPreferencesService(path);
            var expected = new EditorPreferences(true, true, false);

            service.Save(expected);

            Assert.Equal(expected, service.Load());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
