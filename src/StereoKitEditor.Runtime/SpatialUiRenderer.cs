using System.Text.Json;
using StereoKit;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

internal sealed class SpatialUiRenderer(
    VisualResourceCache resources,
    IEditorInteractionResolver interactions,
    RuntimeSessionMode runtimeMode,
    Action<SceneEntity, string> reportError,
    Action<Guid, Guid, JsonElement, string> componentDataCommitted)
{
    private readonly Dictionary<Guid, Pose> _panelPoses = [];
    private readonly Material _layoutHandleMaterial = CreateLayoutHandleMaterial();
    private LayoutResizeDrag? _resizeDrag;
    private LayoutAnchorDrag? _anchorDrag;

    public void Draw(
        SceneEntity panelEntity,
        IReadOnlyList<Guid> selections,
        SceneUiInteractionMode sceneInteractionMode,
        bool wantsPick,
        Ray pickRay,
        ref Guid? pickedEntityId,
        ref float nearestHitDistance)
    {
        var panel = panelEntity.Components.UiPanel;
        if (panel is not { Visible: true })
        {
            return;
        }

        var size = new Vec2(
            panel.AutoWidth ? 0 : (float)Math.Max(0.08, panel.Size.X),
            panel.AutoHeight ? 0 : (float)Math.Max(0.06, panel.Size.Y));
        var layoutSize = new Vector2Value(
            size.x > 0 ? size.x : Math.Max(0.08, panel.Size.X),
            size.y > 0 ? size.y : Math.Max(0.06, panel.Size.Y));
        var layouts = SpatialUiLayoutEngine.Calculate(panelEntity, layoutSize);
        var interactive = runtimeMode == RuntimeSessionMode.Play
            || sceneInteractionMode == SceneUiInteractionMode.Preview;
        var originalFar = UI.EnableFarInteract;
        UI.EnableFarInteract = panel.FarInteraction && interactive;
        UI.PushEnabled(interactive);
        UI.PushId(panelEntity.Id.ToString("N"));
        try
        {
            if (panel.Kind == UiPanelKind.Surface)
            {
                UI.PushSurface(Pose.Identity, Vec3.Zero, size);
                DrawElements(panelEntity.Id, layouts, interactive);
                UI.PopSurface();
            }
            else
            {
                var pose = _panelPoses.TryGetValue(panelEntity.Id, out var current) ? current : Pose.Identity;
                var move = runtimeMode == RuntimeSessionMode.Play && panel.MovableInGame ? UIMove.Exact : UIMove.None;
                UI.WindowBegin(
                    $"{panel.Title}##{panelEntity.Id:N}",
                    ref pose,
                    size,
                    panel.Kind switch
                    {
                        UiPanelKind.BodyOnly => UIWin.Body,
                        UiPanelKind.HeaderOnly => UIWin.Head,
                        _ => UIWin.Normal,
                    },
                    move);
                DrawElements(panelEntity.Id, layouts, interactive);
                UI.WindowEnd();
                _panelPoses[panelEntity.Id] = pose;
            }
        }
        finally
        {
            UI.PopId();
            UI.PopEnabled();
            UI.EnableFarInteract = originalFar;
        }

        if (selections.Contains(panelEntity.Id))
        {
            DrawSelectionRect(Vec3.Zero, new Vec2((float)layoutSize.X, (float)layoutSize.Y));
        }

        foreach (var layout in layouts)
        {
            if (selections.Contains(layout.Entity.Id))
            {
                DrawSelectionRect(layout.Center, layout.Size);
            }
        }

        if (runtimeMode == RuntimeSessionMode.Scene && sceneInteractionMode == SceneUiInteractionMode.Edit)
        {
            UpdateAndDrawLayoutHandles(panelEntity, layouts, layoutSize, selections);
        }
        else
        {
            _resizeDrag = null;
            _anchorDrag = null;
        }

        if (runtimeMode != RuntimeSessionMode.Scene || sceneInteractionMode != SceneUiInteractionMode.Edit || !wantsPick)
        {
            return;
        }

        var candidateEntityId = pickedEntityId;
        var candidateDistance = nearestHitDistance;
        TryPick(panelEntity.Id, Vec3.Zero, new Vec3((float)layoutSize.X, (float)layoutSize.Y, 0.012f));
        foreach (var layout in layouts)
        {
            TryPick(layout.Entity.Id, layout.Center, new Vec3(layout.Size.x, layout.Size.y, 0.016f));
        }

        pickedEntityId = candidateEntityId;
        nearestHitDistance = candidateDistance;

        void TryPick(Guid entityId, Vec3 center, Vec3 boundsSize)
        {
            var worldBounds = WorldBounds(center, boundsSize);
            if (!worldBounds.Intersect(pickRay, out var hit))
            {
                return;
            }

            var distance = Vec3.DistanceSq(pickRay.position, hit);
            if (distance < candidateDistance)
            {
                candidateDistance = distance;
                candidateEntityId = entityId;
            }
        }
    }

    private void DrawElements(Guid panelId, IReadOnlyList<UiElementLayout> layouts, bool interactive)
    {
        foreach (var layout in layouts)
        {
            UI.PushId(layout.Entity.Id.ToString("N"));
            UI.LayoutPush(layout.TopLeft, layout.Size, addMargin: false);
            try
            {
                DrawElement(panelId, layout, interactive);
            }
            finally
            {
                UI.LayoutPop();
                UI.PopId();
            }
        }
    }

    private void DrawElement(Guid panelId, UiElementLayout layout, bool interactive)
    {
        var entity = layout.Entity;
        if (!entity.Enabled)
        {
            return;
        }

        if (entity.Components.UiText is { } text)
        {
            var style = resources.GetTextStyle(text.TextStyleAssetId, null, 0.035, text.Color, out var error);
            if (error is not null) reportError(entity, error);
            UI.PushTextStyle(style);
            UI.PushTint(ToColor(text.Color));
            UI.Text(
                text.Text ?? string.Empty,
                text.Alignment switch
                {
                    TextHorizontalAlignment.Center => Align.TopCenter,
                    TextHorizontalAlignment.Right => Align.TopRight,
                    _ => Align.TopLeft,
                },
                text.Wrap ? TextFit.Wrap : TextFit.Overflow,
                layout.Size);
            UI.PopTint();
            UI.PopTextStyle();
            return;
        }

        if (entity.Components.UiImage is { } image)
        {
            if (resources.TryGetSprite(image.TextureAssetId, out var sprite, out var error))
            {
                UI.PushTint(ToColor(image.Tint));
                UI.Image(sprite, ResolveUiImageSize(image, sprite, layout.Size));
                UI.PopTint();
            }
            else
            {
                reportError(entity, error ?? "The UI image could not be loaded.");
                UI.Label("Missing image", false);
            }

            return;
        }

        if (entity.Components.UiSpacer is not null)
        {
            UI.LayoutReserve(layout.Size, addPadding: false);
            return;
        }

        if (entity.Components.UiSeparator is not null)
        {
            UI.HSeparator();
            return;
        }

        if (entity.Components.UiButton is { } button)
        {
            UI.PushEnabled(button.Enabled && interactive);
            var pressed = button.ImageTextureAssetId is { } imageId
                && resources.TryGetSprite(imageId, out var sprite, out _)
                    ? UI.ButtonImg(button.Label, sprite, UIBtnLayout.Left, layout.Size, Align.Center)
                    : UI.Button(button.Label, layout.Size, Align.Center);
            UI.PopEnabled();
            if (pressed && !string.IsNullOrWhiteSpace(button.ActionId)
                && !interactions.TryInvoke(new(
                    button.ActionId,
                    panelId,
                    entity.Id,
                    runtimeMode == RuntimeSessionMode.Play ? EditorRuntimeMode.Play : EditorRuntimeMode.Scene), out var error))
            {
                reportError(entity, error ?? $"Action '{button.ActionId}' failed.");
            }

            return;
        }

        if (entity.Components.UiToggle is { } toggle)
        {
            var value = toggle.DesignValue;
            if (!string.IsNullOrWhiteSpace(toggle.BindingId)
                && interactions.TryRead(toggle.BindingId, out var bound)
                && bound.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = bound.GetBoolean();
            }

            UI.PushEnabled(toggle.Enabled && interactive);
            var changed = UI.Toggle(toggle.Label, ref value, layout.Size, Align.CenterLeft);
            UI.PopEnabled();
            if (changed && !string.IsNullOrWhiteSpace(toggle.BindingId)
                && !interactions.TryWrite(toggle.BindingId, JsonSerializer.SerializeToElement(value), out var error))
            {
                reportError(entity, error ?? $"Binding '{toggle.BindingId}' could not be written.");
            }

            return;
        }

        if (entity.Components.UiSlider is { } slider)
        {
            var value = slider.DesignValue;
            if (!string.IsNullOrWhiteSpace(slider.BindingId)
                && interactions.TryRead(slider.BindingId, out var bound)
                && bound.ValueKind == JsonValueKind.Number)
            {
                value = bound.GetDouble();
            }

            var minimum = Math.Min(slider.Minimum, slider.Maximum);
            var maximum = Math.Max(slider.Minimum, slider.Maximum);
            value = Math.Clamp(value, minimum, maximum);
            if (!string.IsNullOrWhiteSpace(slider.Label))
            {
                UI.Label(string.IsNullOrWhiteSpace(slider.Units)
                    ? $"{slider.Label}: {value:0.###}"
                    : $"{slider.Label}: {value:0.###} {slider.Units}", false);
                UI.SameLine();
            }

            UI.PushEnabled(slider.Enabled && interactive);
            var changed = UI.HSlider(
                $"##{entity.Id:N}",
                ref value,
                minimum,
                maximum,
                Math.Max(0.000001, slider.Increment),
                layout.Size.x,
                UIConfirm.Pinch,
                UINotify.Change);
            UI.PopEnabled();
            if (changed && !string.IsNullOrWhiteSpace(slider.BindingId)
                && !interactions.TryWrite(slider.BindingId, JsonSerializer.SerializeToElement(value), out var error))
            {
                reportError(entity, error ?? $"Binding '{slider.BindingId}' could not be written.");
            }

            return;
        }

        if (entity.Components.UiTextInput is { } input)
        {
            var value = input.DesignValue ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(input.BindingId)
                && interactions.TryRead(input.BindingId, out var bound)
                && bound.ValueKind == JsonValueKind.String)
            {
                value = bound.GetString() ?? string.Empty;
            }

            value = value[..Math.Min(value.Length, Math.Clamp(input.MaximumLength, 1, 32768))];
            if (!string.IsNullOrWhiteSpace(input.Label))
            {
                UI.Label(input.Label, false);
            }

            UI.PushEnabled(input.Enabled && interactive);
            var changed = UI.Input($"##{entity.Id:N}", ref value, layout.Size, TextContext.Text);
            UI.PopEnabled();
            if (changed && !string.IsNullOrWhiteSpace(input.BindingId)
                && !interactions.TryWrite(input.BindingId, JsonSerializer.SerializeToElement(value), out var error))
            {
                reportError(entity, error ?? $"Binding '{input.BindingId}' could not be written.");
            }
        }
    }

    private static Vec2 ResolveUiImageSize(UiImageComponent image, Sprite sprite, Vec2 bounds)
    {
        if (image.SizingMode is ImageSizingMode.Stretch or ImageSizingMode.Fill)
        {
            return bounds;
        }

        var aspect = Math.Max(0.0001f, sprite.Aspect);
        var fit = bounds.x / Math.Max(0.0001f, bounds.y) > aspect
            ? new Vec2(bounds.y * aspect, bounds.y)
            : new Vec2(bounds.x, bounds.x / aspect);
        return image.SizingMode == ImageSizingMode.NativePixels
            ? new Vec2(sprite.Width / 1000f, sprite.Height / 1000f)
            : fit;
    }

    private static Color ToColor(ColorValue color) => new(
        (float)Math.Clamp(color.R, 0, 1),
        (float)Math.Clamp(color.G, 0, 1),
        (float)Math.Clamp(color.B, 0, 1),
        (float)Math.Clamp(color.A, 0, 1));

    private void UpdateAndDrawLayoutHandles(
        SceneEntity panelEntity,
        IReadOnlyList<UiElementLayout> layouts,
        Vector2Value panelSize,
        IReadOnlyList<Guid> selections)
    {
        var selectedId = selections.FirstOrDefault();
        if (selectedId == Guid.Empty)
        {
            _resizeDrag = null;
            _anchorDrag = null;
            return;
        }

        SceneEntity? target;
        Vec3 center;
        Vec2 size;
        UiElementLayout? elementLayout = null;
        if (selectedId == panelEntity.Id)
        {
            target = panelEntity;
            center = Vec3.Zero;
            size = new((float)panelSize.X, (float)panelSize.Y);
        }
        else
        {
            elementLayout = layouts.FirstOrDefault(layout => layout.Entity.Id == selectedId);
            target = elementLayout?.Entity;
            if (target is null)
            {
                return;
            }

            center = elementLayout!.Center;
            size = elementLayout.Size;
        }

        var plane = CapturePanelPlane();
        var handleSize = Math.Clamp(Math.Min(size.x, size.y) * 0.09f, 0.007f, 0.018f);
        var corners = LayoutCorners(center, size);
        var pointerRay = Input.Mouse.Ray;
        var left = Input.Key(Key.MouseLeft);

        for (var index = 0; index < corners.Length; index++)
        {
            var isHot = _resizeDrag?.CornerIndex == index;
            DrawLayoutHandle(corners[index], handleSize, isHot
                ? new Color(1, 0.86f, 0.28f, 1)
                : new Color(1, 0.64f, 0.12f, 1));
        }

        if (_resizeDrag is null && _anchorDrag is null && left.IsJustActive())
        {
            var hoveredCorner = ClosestHandle(pointerRay, corners, handleSize * 1.7f);
            if (hoveredCorner >= 0)
            {
                var component = target.Components.FindByType(
                    target.Id == panelEntity.Id ? BuiltInComponentTypes.UiPanel : BuiltInComponentTypes.UiRect);
                if (component is not null)
                {
                    _resizeDrag = new(
                        target,
                        component.Id,
                        component.Data.Clone(),
                        hoveredCorner,
                        center,
                        size,
                        plane);
                }
            }
        }

        if (_resizeDrag is { } resize)
        {
            if (Input.Key(Key.Esc).IsJustActive())
            {
                RestoreComponent(resize.Entity, resize.ComponentId, resize.OriginalData);
                _resizeDrag = null;
            }
            else if (left.IsActive() && TryPanelPoint(pointerRay, resize.Plane, out var point))
            {
                var nextSize = new Vec2(
                    MathF.Max(0.02f, MathF.Abs(point.x - resize.Center.x) * 2),
                    MathF.Max(0.02f, MathF.Abs(point.y - resize.Center.y) * 2));
                ApplyResize(resize.Entity, nextSize);
            }
            else if (left.IsJustInactive())
            {
                CommitComponent(resize.Entity, resize.ComponentId, "UI Bounds");
                _resizeDrag = null;
            }
        }

        if (elementLayout is null || target.Components.UiRect is not { LayoutMode: UiLayoutMode.Absolute } rect)
        {
            return;
        }

        var anchorPoint = SpatialUiLayoutEngine.AnchorPoint(rect.Anchor, elementLayout.ParentRegion);
        var anchor = new Vec3(anchorPoint.x, anchorPoint.y, center.z - 0.002f);
        DrawOverlaySegment(anchor, center, new Color(0.30f, 0.82f, 1, 0.85f), handleSize * 0.18f);
        DrawAnchorHandle(anchor, handleSize * 0.8f, _anchorDrag is not null);

        if (_resizeDrag is null && _anchorDrag is null && left.IsJustActive()
            && ClosestHandle(pointerRay, [anchor], handleSize * 1.6f) == 0)
        {
            var component = target.Components.FindByType(BuiltInComponentTypes.UiRect);
            if (component is not null)
            {
                _anchorDrag = new(
                    target,
                    component.Id,
                    component.Data.Clone(),
                    elementLayout,
                    rect,
                    plane);
            }
        }

        if (_anchorDrag is { } anchorDrag)
        {
            if (Input.Key(Key.Esc).IsJustActive())
            {
                RestoreComponent(anchorDrag.Entity, anchorDrag.ComponentId, anchorDrag.OriginalData);
                _anchorDrag = null;
            }
            else if (left.IsActive() && TryPanelPoint(pointerRay, anchorDrag.Plane, out var point))
            {
                ApplyNearestAnchor(anchorDrag, point);
            }
            else if (left.IsJustInactive())
            {
                CommitComponent(anchorDrag.Entity, anchorDrag.ComponentId, "UI Anchor");
                _anchorDrag = null;
            }
        }
    }

    private static Vec3[] LayoutCorners(Vec3 center, Vec2 size)
    {
        var half = size * 0.5f;
        return
        [
            center + new Vec3(-half.x, half.y, -0.010f),
            center + new Vec3(half.x, half.y, -0.010f),
            center + new Vec3(half.x, -half.y, -0.010f),
            center + new Vec3(-half.x, -half.y, -0.010f),
        ];
    }

    private void ApplyResize(SceneEntity entity, Vec2 size)
    {
        if (entity.Components.UiPanel is { } panel)
        {
            entity.Components.UiPanel = panel with
            {
                Size = new(size.x, size.y),
                AutoWidth = false,
                AutoHeight = false,
            };
            return;
        }

        if (entity.Components.UiRect is { } rect)
        {
            entity.Components.UiRect = rect.LayoutMode == UiLayoutMode.Absolute
                ? rect with { Size = new(size.x, size.y), StretchWidth = false, StretchHeight = false }
                : rect with { PreferredSize = new(size.x, size.y), StretchWidth = false, StretchHeight = false };
        }
    }

    private static void ApplyNearestAnchor(LayoutAnchorDrag drag, Vec3 point)
    {
        var candidates = Enum.GetValues<UiAnchor>()
            .Select(value => (Value: value, Point: SpatialUiLayoutEngine.AnchorPoint(value, drag.Layout.ParentRegion)))
            .ToArray();
        var nearest = candidates.MinBy(candidate =>
            MathF.Pow(point.x - candidate.Point.x, 2) + MathF.Pow(point.y - candidate.Point.y, 2));
        var layout = drag.Layout;
        var start = drag.StartRect;
        var left = layout.Center.x - (layout.Size.x * 0.5f);
        var top = layout.Center.y + (layout.Size.y * 0.5f);
        var positionX = left - nearest.Point.x + ((float)start.Pivot.X * layout.Size.x) - (float)start.Margin.Left;
        var positionY = nearest.Point.y + ((float)start.Pivot.Y * layout.Size.y) - (float)start.Margin.Top - top;
        drag.Entity.Components.UiRect = start with
        {
            Anchor = nearest.Value,
            Position = new(positionX, positionY),
        };
    }

    private void CommitComponent(SceneEntity entity, Guid componentId, string description)
    {
        if (entity.Components.Find(componentId) is { } component)
        {
            componentDataCommitted(entity.Id, componentId, component.Data.Clone(), description);
        }
    }

    private static void RestoreComponent(SceneEntity entity, Guid componentId, JsonElement data)
    {
        if (entity.Components.Find(componentId) is { } component)
        {
            component.Data = data.Clone();
        }
    }

    private void DrawLayoutHandle(Vec3 center, float size, Color color) =>
        Mesh.Cube.Draw(_layoutHandleMaterial, Matrix.TRS(center, Quat.Identity, new Vec3(size, size, size * 0.35f)), color);

    private void DrawAnchorHandle(Vec3 center, float size, bool hot)
    {
        var color = hot ? new Color(0.55f, 0.95f, 1, 1) : new Color(0.22f, 0.76f, 1, 1);
        DrawOverlaySegment(center - new Vec3(size, 0, 0), center + new Vec3(size, 0, 0), color, size * 0.35f);
        DrawOverlaySegment(center - new Vec3(0, size, 0), center + new Vec3(0, size, 0), color, size * 0.35f);
    }

    private void DrawOverlaySegment(Vec3 start, Vec3 end, Color color, float thickness)
    {
        var delta = end - start;
        Mesh.Cube.Draw(
            _layoutHandleMaterial,
            Matrix.TRS(
                (start + end) * 0.5f,
                Quat.LookDir(delta.LengthSq > 0.000001f ? delta.Normalized : Vec3.Forward),
                new Vec3(thickness, thickness, MathF.Max(thickness, delta.Length))),
            color);
    }

    private static int ClosestHandle(Ray ray, IReadOnlyList<Vec3> localPoints, float localRadius)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var index = 0; index < localPoints.Count; index++)
        {
            var worldPoint = Hierarchy.ToWorld(localPoints[index]);
            var worldRadiusPoint = Hierarchy.ToWorld(localPoints[index] + new Vec3(localRadius, 0, 0));
            var radius = MathF.Max(0.004f, Vec3.Distance(worldPoint, worldRadiusPoint));
            var distance = DistanceRayToPoint(ray, worldPoint);
            if (distance <= radius && distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static PanelPlane CapturePanelPlane()
    {
        var origin = Hierarchy.ToWorld(Vec3.Zero);
        var right = Hierarchy.ToWorld(Vec3.Right) - origin;
        var up = Hierarchy.ToWorld(Vec3.Up) - origin;
        var normal = Vec3.Cross(right, up).Normalized;
        return new(origin, right, up, normal);
    }

    private static bool TryPanelPoint(Ray ray, PanelPlane plane, out Vec3 point)
    {
        var denominator = Vec3.Dot(ray.direction, plane.Normal);
        if (MathF.Abs(denominator) < 0.00001f)
        {
            point = Vec3.Zero;
            return false;
        }

        var distance = Vec3.Dot(plane.Origin - ray.position, plane.Normal) / denominator;
        if (distance < 0)
        {
            point = Vec3.Zero;
            return false;
        }

        var world = ray.position + (ray.direction * distance);
        var delta = world - plane.Origin;
        point = new(
            Vec3.Dot(delta, plane.Right) / MathF.Max(0.000001f, plane.Right.LengthSq),
            Vec3.Dot(delta, plane.Up) / MathF.Max(0.000001f, plane.Up.LengthSq),
            0);
        return true;
    }

    private static float DistanceRayToPoint(Ray ray, Vec3 point)
    {
        var distanceAlongRay = MathF.Max(0, Vec3.Dot(point - ray.position, ray.direction));
        return Vec3.Distance(ray.position + (ray.direction * distanceAlongRay), point);
    }

    private static Material CreateLayoutHandleMaterial()
    {
        var material = Material.Default.Copy();
        material.DepthTest = DepthTest.Always;
        material.DepthWrite = false;
        material.QueueOffset = 100;
        return material;
    }

    private static void DrawSelectionRect(Vec3 center, Vec2 size)
    {
        var color = new Color(1, 0.64f, 0.12f, 1);
        var half = size * 0.5f;
        var topLeft = center + new Vec3(-half.x, half.y, -0.008f);
        var topRight = center + new Vec3(half.x, half.y, -0.008f);
        var bottomRight = center + new Vec3(half.x, -half.y, -0.008f);
        var bottomLeft = center + new Vec3(-half.x, -half.y, -0.008f);
        Lines.Add(topLeft, topRight, color, 0.002f);
        Lines.Add(topRight, bottomRight, color, 0.002f);
        Lines.Add(bottomRight, bottomLeft, color, 0.002f);
        Lines.Add(bottomLeft, topLeft, color, 0.002f);
    }

    private static Bounds WorldBounds(Vec3 center, Vec3 size)
    {
        var half = size * 0.5f;
        var minimum = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var maximum = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var x in new[] { center.x - half.x, center.x + half.x })
        foreach (var y in new[] { center.y - half.y, center.y + half.y })
        foreach (var z in new[] { center.z - half.z, center.z + half.z })
        {
            var point = Hierarchy.ToWorld(new Vec3(x, y, z));
            minimum = new(MathF.Min(minimum.x, point.x), MathF.Min(minimum.y, point.y), MathF.Min(minimum.z, point.z));
            maximum = new(MathF.Max(maximum.x, point.x), MathF.Max(maximum.y, point.y), MathF.Max(maximum.z, point.z));
        }

        return Bounds.FromCorners(minimum, maximum);
    }
}

internal sealed record UiElementLayout(
    SceneEntity Entity,
    Vec3 TopLeft,
    Vec3 Center,
    Vec2 Size,
    UiLayoutRegion ParentRegion);

internal readonly record struct UiLayoutRegion(float Left, float Top, float Width, float Height);

internal sealed record LayoutResizeDrag(
    SceneEntity Entity,
    Guid ComponentId,
    JsonElement OriginalData,
    int CornerIndex,
    Vec3 Center,
    Vec2 StartSize,
    PanelPlane Plane);

internal sealed record LayoutAnchorDrag(
    SceneEntity Entity,
    Guid ComponentId,
    JsonElement OriginalData,
    UiElementLayout Layout,
    UiRectComponent StartRect,
    PanelPlane Plane);

internal readonly record struct PanelPlane(Vec3 Origin, Vec3 Right, Vec3 Up, Vec3 Normal);

internal static class SpatialUiLayoutEngine
{
    public static IReadOnlyList<UiElementLayout> Calculate(SceneEntity panel, Vector2Value panelSize)
    {
        var result = new List<UiElementLayout>();
        LayoutChildren(panel.Children, new UiLayoutRegion(
            -(float)panelSize.X * 0.5f,
            (float)panelSize.Y * 0.5f,
            (float)panelSize.X,
            (float)panelSize.Y));
        return result;

        void LayoutChildren(IReadOnlyList<SceneEntity> children, UiLayoutRegion region)
        {
            var cursorX = region.Left;
            var cursorY = region.Top;
            var rowHeight = 0f;
            foreach (var child in children)
            {
                var rect = child.Components.UiRect ?? new UiRectComponent();
                var width = (float)Math.Max(rect.MinimumSize.X, rect.LayoutMode == UiLayoutMode.Absolute ? rect.Size.X : rect.PreferredSize.X);
                var height = (float)Math.Max(rect.MinimumSize.Y, rect.LayoutMode == UiLayoutMode.Absolute ? rect.Size.Y : rect.PreferredSize.Y);
                if (rect.StretchWidth) width = Math.Max(width, region.Width - (float)(rect.Margin.Left + rect.Margin.Right));
                if (rect.StretchHeight) height = Math.Max(height, region.Height - (float)(rect.Margin.Top + rect.Margin.Bottom));
                width = Math.Max(0.001f, width);
                height = Math.Max(0.001f, height);

                float left;
                float top;
                if (rect.LayoutMode == UiLayoutMode.Absolute)
                {
                    var anchor = AnchorPoint(rect.Anchor, region);
                    left = anchor.x + (float)rect.Position.X - ((float)rect.Pivot.X * width) + (float)rect.Margin.Left;
                    top = anchor.y - (float)rect.Position.Y + ((float)rect.Pivot.Y * height) - (float)rect.Margin.Top;
                }
                else
                {
                    if (!rect.SameLine && cursorX > region.Left)
                    {
                        cursorX = region.Left;
                        cursorY -= rowHeight;
                        rowHeight = 0;
                    }

                    left = cursorX + (float)rect.Margin.Left;
                    top = cursorY - (float)rect.Margin.Top;
                    cursorX = left + width + (float)rect.Margin.Right;
                    rowHeight = Math.Max(rowHeight, height + (float)(rect.Margin.Top + rect.Margin.Bottom));
                    if (rect.LineBreak)
                    {
                        cursorX = region.Left;
                        cursorY -= rowHeight;
                        rowHeight = 0;
                    }
                }

                var center = new Vec3(left + (width * 0.5f), top - (height * 0.5f), -0.004f);
                var topLeft = new Vec3(left, top, -0.004f);
                var layout = new UiElementLayout(child, topLeft, center, new Vec2(width, height), region);
                result.Add(layout);
                if (child.Children.Count > 0)
                {
                    LayoutChildren(child.Children, new UiLayoutRegion(
                        left + (float)rect.Padding.Left,
                        top - (float)rect.Padding.Top,
                        Math.Max(0.001f, width - (float)(rect.Padding.Left + rect.Padding.Right)),
                        Math.Max(0.001f, height - (float)(rect.Padding.Top + rect.Padding.Bottom))));
                }
            }
        }
    }

    internal static Vec2 AnchorPoint(UiAnchor anchor, UiLayoutRegion region)
    {
        var x = anchor switch
        {
            UiAnchor.TopCenter or UiAnchor.Center or UiAnchor.BottomCenter => region.Left + (region.Width * 0.5f),
            UiAnchor.TopRight or UiAnchor.CenterRight or UiAnchor.BottomRight => region.Left + region.Width,
            _ => region.Left,
        };
        var y = anchor switch
        {
            UiAnchor.CenterLeft or UiAnchor.Center or UiAnchor.CenterRight => region.Top - (region.Height * 0.5f),
            UiAnchor.BottomLeft or UiAnchor.BottomCenter or UiAnchor.BottomRight => region.Top - region.Height,
            _ => region.Top,
        };
        return new(x, y);
    }
}
