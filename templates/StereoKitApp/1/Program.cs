using StereoKit;
using StereoKitEditor.Runtime;

namespace __PROJECT_NAME__;

internal static class Program
{
    private static int Main(string[] args) =>
        EditorRuntimeHost.IsEditorLaunch(args)
            ? EditorRuntimeHost.Run(args, new EditorAdapter())
            : RunStandalone();

    private static int RunStandalone()
    {
        var settings = new SKSettings
        {
            appName = "__PROJECT_NAME__",
            mode = AppMode.Simulator,
            flatscreenWidth = 900,
            flatscreenHeight = 600,
            standbyMode = StandbyMode.None,
        };
        if (!SK.Initialize(settings))
        {
            return 1;
        }

        var material = Material.Default.Copy();
        SK.Run(() =>
        {
            var rotation = Quat.FromAngles(0, Time.Totalf * 30, 0);
            Mesh.Cube.Draw(
                material,
                Matrix.TRS(new Vec3(0, 0, -0.65f), rotation, new Vec3(0.2f)),
                new Color(0.18f, 0.68f, 0.92f, 1));
        });
        return 0;
    }
}
