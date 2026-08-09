using System.Xml.Linq;

namespace StereoKitEditor.Tests;

public sealed class ProjectEntryMenuTests
{
    [Fact]
    public void LauncherOpenProjectMenu_SeparatesDescriptorOpenFromStereoKitImport()
    {
        var items = ReadOpenProjectMenu("src", "StereoKitEditor.App", "ProjectLauncherWindow.axaml");

        Assert.Collection(
            items,
            item => Assert.Equal(("Open SKinny Editor Project…", "HandleBrowseDescriptor"), item),
            item => Assert.Equal(("Import Existing StereoKit Project…", "HandleImport"), item));
    }

    [Fact]
    public void EditorOpenProjectMenu_OffersOpenImportAndRecentActions()
    {
        var items = ReadOpenProjectMenu("src", "StereoKitEditor.App", "MainWindow.axaml");

        Assert.Collection(
            items,
            item => Assert.Equal(("Open SKinny Editor Project…", "OpenProject_Click"), item),
            item => Assert.Equal(("Import Existing StereoKit Project…", "ImportProject_Click"), item),
            item => Assert.Equal(("Recent Projects…", "RecentProjects_Click"), item));
    }

    private static IReadOnlyList<(string Header, string Handler)> ReadOpenProjectMenu(params string[] pathParts)
    {
        var path = Path.Combine([FindWorkspaceRoot(), .. pathParts]);
        var document = XDocument.Load(path);
        var menu = document
            .Descendants()
            .Single(element => element.Name.LocalName == "DropDownButton"
                               && (string?)element.Attribute("Content") == "Open Project");

        return menu
            .Descendants()
            .Where(element => element.Name.LocalName == "MenuItem")
            .Select(element => (
                (string?)element.Attribute("Header") ?? string.Empty,
                (string?)element.Attribute("Click") ?? string.Empty))
            .ToArray();
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StereoKitEditor.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate StereoKitEditor.sln from the test output directory.");
    }
}
