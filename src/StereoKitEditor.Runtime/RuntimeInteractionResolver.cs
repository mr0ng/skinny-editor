using System.Text.Json;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;

namespace StereoKitEditor.Runtime;

internal sealed class RuntimeInteractionResolver(
    EditorAdapterBuilder builder,
    RuntimeSessionMode mode,
    Func<SceneUiInteractionMode> sceneMode) : IEditorInteractionResolver
{
    private readonly Dictionary<string, JsonElement> _sceneDesignValues = new(StringComparer.Ordinal);

    public bool TryRead(string bindingId, out JsonElement value)
    {
        if (!builder.TryGetBinding(bindingId, out var registration)
            || !Supports(registration.Descriptor.Modes))
        {
            value = default;
            return false;
        }

        if (mode == RuntimeSessionMode.Scene)
        {
            if (sceneMode() != SceneUiInteractionMode.Preview)
            {
                value = default;
                return false;
            }

            value = _sceneDesignValues.TryGetValue(bindingId, out var existing)
                ? existing.Clone()
                : registration.Descriptor.DesignValue.Clone();
            return true;
        }

        try
        {
            value = registration.Read().Clone();
            return IsCompatible(registration.Descriptor.Kind, value);
        }
        catch
        {
            value = default;
            return false;
        }
    }

    public bool TryWrite(string bindingId, JsonElement value, out string? error)
    {
        error = null;
        if (!builder.TryGetBinding(bindingId, out var registration))
        {
            error = $"Binding '{bindingId}' is not registered.";
            return false;
        }

        if (!Supports(registration.Descriptor.Modes))
        {
            error = $"Binding '{bindingId}' is not available in this runtime mode.";
            return false;
        }

        if (!IsCompatible(registration.Descriptor.Kind, value))
        {
            error = $"Binding '{bindingId}' received an incompatible value.";
            return false;
        }

        if (registration.Descriptor.IsReadOnly)
        {
            error = $"Binding '{bindingId}' is read-only.";
            return false;
        }

        if (mode == RuntimeSessionMode.Scene)
        {
            if (sceneMode() != SceneUiInteractionMode.Preview)
            {
                error = "UI values can be changed only in Scene Preview mode.";
                return false;
            }

            _sceneDesignValues[bindingId] = value.Clone();
            return true;
        }

        if (registration.Write is null)
        {
            error = $"Binding '{bindingId}' has no writer.";
            return false;
        }

        try
        {
            registration.Write(value);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryInvoke(EditorActionInvocation invocation, out string? error)
    {
        error = null;
        if (!builder.TryGetAction(invocation.ActionId, out var registration))
        {
            error = $"Action '{invocation.ActionId}' is not registered.";
            return false;
        }

        if (mode == RuntimeSessionMode.Scene)
        {
            error = "Project actions are disabled in Scene Preview mode.";
            return false;
        }

        if (!Supports(registration.Descriptor.Modes))
        {
            error = $"Action '{invocation.ActionId}' is not available in Play.";
            return false;
        }

        try
        {
            registration.Invoke(invocation);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool Supports(EditorInteractionModes modes) => mode == RuntimeSessionMode.Scene
        ? modes.HasFlag(EditorInteractionModes.ScenePreview)
        : modes.HasFlag(EditorInteractionModes.Play);

    private static bool IsCompatible(EditorBindingValueKind kind, JsonElement value) => kind switch
    {
        EditorBindingValueKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        EditorBindingValueKind.Number => value.ValueKind == JsonValueKind.Number,
        EditorBindingValueKind.String => value.ValueKind == JsonValueKind.String,
        _ => false,
    };
}
