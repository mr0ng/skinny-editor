using StereoKitEditor.Runtime;

namespace StereoKitEditor.PreviewHost;

internal static class EntryPoint
{
    private static int Main(string[] args) =>
        EditorRuntimeHost.Run(args, new DefaultEditorRuntimeExtension("SKinny Editor"));
}
