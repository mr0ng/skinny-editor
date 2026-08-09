using StereoKitEditor.Adapter;

namespace __PROJECT_NAME__;

internal sealed class EditorAdapter : IEditorProjectAdapter
{
    public string Id => "local.__PROJECT_SLUG__";
    public string DisplayName => "__PROJECT_NAME__";
    public string Version => "0.1.0";

    public void Configure(EditorAdapterBuilder builder)
    {
        // Register project-owned components, bindings, and actions here.
    }

    public void Initialize(EditorProjectRuntimeContext context)
    {
    }

    public void Step(EditorProjectRuntimeContext context)
    {
    }

    public void Shutdown(EditorProjectRuntimeContext context)
    {
    }
}
