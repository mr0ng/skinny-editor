using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

public interface IEditorRuntimeExtension
{
    string DisplayName { get; }

    void Initialize(EditorRuntimeContext context);

    void Step(EditorRuntimeContext context);

    void Shutdown(EditorRuntimeContext context);
}

public sealed class EditorRuntimeContext
{
    internal EditorRuntimeContext(RuntimeSessionMode mode, RuntimePlayState playState, SceneDocument scene)
    {
        Mode = mode;
        PlayState = playState;
        Scene = scene;
    }

    public RuntimeSessionMode Mode { get; internal set; }
    public RuntimePlayState PlayState { get; internal set; }
    public SceneDocument Scene { get; internal set; }
    public float SimulationTime { get; internal set; }
}

public sealed class DefaultEditorRuntimeExtension(string displayName) : IEditorRuntimeExtension
{
    public string DisplayName { get; } = displayName;

    public void Initialize(EditorRuntimeContext context)
    {
    }

    public void Step(EditorRuntimeContext context)
    {
    }

    public void Shutdown(EditorRuntimeContext context)
    {
    }
}
