using System.Diagnostics;
using StereoKit;
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Scene;

namespace StereoKitEditor.Runtime;

internal sealed class SceneViewportController(
    Action<SceneCameraState> cameraChanged,
    Action<SceneToolSettings> toolSettingsChanged,
    Action<Guid, TransformComponent> transformCommitted,
    Action<IReadOnlyList<EntityTransformValue>> transformsCommitted,
    Action<IReadOnlyList<Guid>> duplicateSelectionRequested,
    Func<SceneEntity, EditorPickBounds?> localBounds)
{
    private readonly object _requestGate = new();
    private readonly Material _gizmoOverlayMaterial = CreateGizmoOverlayMaterial();
    private readonly WindowsViewportMetrics _viewportMetrics = new();
    private SceneCameraState _camera = SceneCameraState.Default;
    private SceneCameraState? _requestedCamera;
    private SceneToolSettings _settings = SceneToolSettings.Default;
    private SceneToolSettings? _requestedSettings;
    private Guid? _frameRequest;
    private bool _hasFrameRequest;
    private long _lastCameraSentTimestamp;
    private bool _cameraWasManipulated;
    private GizmoDrag? _drag;
    private PendingDuplicateDrag? _pendingDuplicateDrag;

    public SceneToolSettings ToolSettings => _settings;

    public void SetCamera(SceneCameraState camera)
    {
        lock (_requestGate)
        {
            _requestedCamera = Sanitize(camera);
        }
    }

    public void SetToolSettings(SceneToolSettings settings)
    {
        lock (_requestGate)
        {
            _requestedSettings = Sanitize(settings);
        }
    }

    public void Frame(Guid? entityId)
    {
        lock (_requestGate)
        {
            _frameRequest = entityId;
            _hasFrameRequest = true;
        }
    }

    public bool Step(
        SceneDocument scene,
        Guid? selectedEntityId,
        IReadOnlyList<Guid> selectedEntityIds,
        long revision)
    {
        ApplyRequests(scene, selectedEntityId);
        if (_settings.UiInteractionMode == SceneUiInteractionMode.Preview)
        {
            ResetPointerState();
            ApplyCameraTransform();
            DrawGrid();
            return true;
        }

        UpdateToolShortcuts();
        var cameraConsumedPointer = UpdateCamera(scene, selectedEntityId);
        ApplyCameraTransform();
        DrawGrid();
        var viewWidgetConsumedPointer = UpdateAndDrawViewWidget();
        var gizmoConsumedPointer = UpdateAndDrawGizmo(
            scene,
            selectedEntityId,
            selectedEntityIds,
            revision,
            allowInteraction: !cameraConsumedPointer && !viewWidgetConsumedPointer);
        return cameraConsumedPointer || viewWidgetConsumedPointer || gizmoConsumedPointer;
    }

    public void ResetPointerState()
    {
        Input.MouseMode = MouseMode.Normal;
        _drag = null;
        _pendingDuplicateDrag = null;
        _cameraWasManipulated = false;
    }

    private void ApplyRequests(SceneDocument scene, Guid? selectedEntityId)
    {
        SceneCameraState? camera;
        SceneToolSettings? settings;
        Guid? frameRequest;
        bool hasFrameRequest;
        lock (_requestGate)
        {
            camera = _requestedCamera;
            settings = _requestedSettings;
            frameRequest = _frameRequest;
            hasFrameRequest = _hasFrameRequest;
            _requestedCamera = null;
            _requestedSettings = null;
            _frameRequest = null;
            _hasFrameRequest = false;
        }

        if (camera is not null)
        {
            _camera = camera;
        }

        if (settings is not null)
        {
            _settings = settings;
        }

        if (hasFrameRequest)
        {
            FrameEntity(scene, frameRequest ?? selectedEntityId);
        }
    }

    private void UpdateToolShortcuts()
    {
        var cameraGesture = Input.Key(Key.MouseRight).IsActive()
            || Input.Key(Key.MouseCenter).IsActive()
            || (Input.Key(Key.Alt).IsActive() && Input.Key(Key.MouseLeft).IsActive());
        if (_drag is not null || cameraGesture)
        {
            return;
        }

        SceneTransformTool? requestedTool = null;
        if (Input.Key(Key.W).IsJustActive()) requestedTool = SceneTransformTool.Move;
        if (Input.Key(Key.E).IsJustActive()) requestedTool = SceneTransformTool.Rotate;
        if (Input.Key(Key.R).IsJustActive()) requestedTool = SceneTransformTool.Scale;
        if (requestedTool is not { } tool || tool == _settings.Tool)
        {
            return;
        }

        _settings = _settings with { Tool = tool };
        toolSettingsChanged(_settings);
    }

    private bool UpdateCamera(SceneDocument scene, Guid? selectedEntityId)
    {
        if (Input.Key(Key.F).IsJustActive())
        {
            FrameEntity(scene, selectedEntityId);
        }

        var right = Input.Key(Key.MouseRight);
        var middle = Input.Key(Key.MouseCenter);
        var altLeft = Input.Key(Key.Alt).IsActive() && Input.Key(Key.MouseLeft).IsActive();
        var changed = false;
        var completed = right.IsJustInactive() || middle.IsJustInactive()
            || (Input.Key(Key.Alt).IsJustInactive() && Input.Key(Key.MouseLeft).IsActive())
            || (Input.Key(Key.MouseLeft).IsJustInactive() && Input.Key(Key.Alt).IsActive())
            || Input.Key(Key.Left).IsJustInactive()
            || Input.Key(Key.Right).IsJustInactive()
            || Input.Key(Key.Up).IsJustInactive()
            || Input.Key(Key.Down).IsJustInactive();

        if (right.IsActive())
        {
            Input.MouseMode = MouseMode.Relative;
            changed |= ApplyFlyLookAndMovement();
        }
        else
        {
            Input.MouseMode = MouseMode.Normal;
        }

        if (altLeft)
        {
            var delta = Input.Mouse.posChange;
            if (MathF.Abs(delta.x) > 0.001f || MathF.Abs(delta.y) > 0.001f)
            {
                _camera = _camera with
                {
                    YawDegrees = _camera.YawDegrees - (delta.x * 0.18),
                    PitchDegrees = Math.Clamp(_camera.PitchDegrees - (delta.y * 0.18), -89, 89),
                };
                changed = true;
            }
        }

        if (middle.IsActive())
        {
            var delta = Input.Mouse.posChange;
            if (MathF.Abs(delta.x) > 0.001f || MathF.Abs(delta.y) > 0.001f)
            {
                var rotation = CameraRotation();
                var scale = (float)_camera.Distance * 0.0015f;
                var offset = ((rotation * Vec3.Right) * (-delta.x * scale))
                    + ((rotation * Vec3.Up) * (delta.y * scale));
                _camera = _camera with { Pivot = FromVec3(ToVec3(_camera.Pivot) + offset) };
                changed = true;
            }
        }

        changed |= ApplyArrowKeyMovement();

        var wheel = Input.Mouse.scrollChange;
        if (MathF.Abs(wheel) > 0.001f)
        {
            // StereoKit exposes the Windows wheel delta, where one ordinary
            // detent is 120 rather than 1. Preserve high-resolution partial
            // deltas and cap a single frame so a queued burst cannot teleport.
            var wheelNotches = Math.Clamp(wheel / 120.0f, -4, 4);
            _camera = _camera with
            {
                Distance = Math.Clamp(
                    _camera.Distance * Math.Exp(-wheelNotches * 0.12),
                    0.05,
                    100),
            };
            changed = true;
            completed = true;
        }

        if (changed)
        {
            _camera = Sanitize(_camera);
            _cameraWasManipulated = true;
            PublishCamera(force: completed);
        }
        else if (completed && _cameraWasManipulated)
        {
            _cameraWasManipulated = false;
            PublishCamera(force: true);
        }

        return right.IsActive() || middle.IsActive() || altLeft;
    }

    private bool ApplyArrowKeyMovement()
    {
        var rotation = CameraRotation();
        var movement = Vec3.Zero;
        if (Input.Key(Key.Up).IsActive()) movement += rotation * Vec3.Forward;
        if (Input.Key(Key.Down).IsActive()) movement -= rotation * Vec3.Forward;
        if (Input.Key(Key.Right).IsActive()) movement += rotation * Vec3.Right;
        if (Input.Key(Key.Left).IsActive()) movement -= rotation * Vec3.Right;
        if (movement.LengthSq < 0.0001f)
        {
            return false;
        }

        movement.Normalize();
        var speed = MathF.Max(0.25f, (float)_camera.Distance) * 1.2f;
        if (Input.Key(Key.Shift).IsActive())
        {
            speed *= 3;
        }

        _camera = _camera with
        {
            Pivot = FromVec3(ToVec3(_camera.Pivot) + (movement * speed * Time.Stepf)),
        };
        return true;
    }

    private bool ApplyFlyLookAndMovement()
    {
        var changed = false;
        var delta = Input.Mouse.posChange;
        var cameraPosition = CameraPosition();
        if (MathF.Abs(delta.x) > 0.001f || MathF.Abs(delta.y) > 0.001f)
        {
            _camera = _camera with
            {
                YawDegrees = _camera.YawDegrees - (delta.x * 0.16),
                PitchDegrees = Math.Clamp(_camera.PitchDegrees - (delta.y * 0.16), -89, 89),
            };
            var forward = CameraRotation() * Vec3.Forward;
            _camera = _camera with { Pivot = FromVec3(cameraPosition + (forward * (float)_camera.Distance)) };
            changed = true;
        }

        var rotation = CameraRotation();
        var movement = Vec3.Zero;
        if (Input.Key(Key.W).IsActive()) movement += rotation * Vec3.Forward;
        if (Input.Key(Key.S).IsActive()) movement -= rotation * Vec3.Forward;
        if (Input.Key(Key.D).IsActive()) movement += rotation * Vec3.Right;
        if (Input.Key(Key.A).IsActive()) movement -= rotation * Vec3.Right;
        if (Input.Key(Key.E).IsActive()) movement += Vec3.Up;
        if (Input.Key(Key.Q).IsActive()) movement -= Vec3.Up;
        if (movement.LengthSq > 0.0001f)
        {
            movement.Normalize();
            var speed = MathF.Max(0.25f, (float)_camera.Distance) * 1.8f;
            if (Input.Key(Key.Shift).IsActive())
            {
                speed *= 3;
            }

            var offset = movement * speed * Time.Stepf;
            _camera = _camera with { Pivot = FromVec3(ToVec3(_camera.Pivot) + offset) };
            changed = true;
        }

        return changed;
    }

    private void ApplyCameraTransform()
    {
        var rotation = CameraRotation();
        Renderer.CameraRoot = Matrix.TR(CameraPosition(), rotation);
        Renderer.Projection = _camera.Projection == SceneProjection.Orthographic
            ? Projection.Ortho
            : Projection.Perspective;
        if (_camera.Projection == SceneProjection.Orthographic)
        {
            Renderer.OrthoSize = Math.Clamp((float)_camera.Distance * 1.15f, 0.05f, 100f);
        }
    }

    private void DrawGrid()
    {
        if (!_settings.ShowGrid)
        {
            return;
        }

        var distance = (float)_camera.Distance;
        var magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(MathF.Max(0.001f, distance))));
        var spacing = magnitude / 5;
        if (distance / spacing > 30)
        {
            spacing *= 2;
        }

        var center = ToVec3(_camera.Pivot);
        var extent = spacing * 20;
        var originX = MathF.Round(center.x / spacing) * spacing;
        var originZ = MathF.Round(center.z / spacing) * spacing;
        var minor = new Color32(60, 68, 78, 150);
        var major = new Color32(86, 97, 110, 185);
        for (var index = -20; index <= 20; index++)
        {
            var coordinate = index * spacing;
            var color = index % 5 == 0 ? major : minor;
            var thickness = index % 5 == 0 ? spacing * 0.012f : spacing * 0.006f;
            Lines.Add(
                new Vec3(originX - extent, 0, originZ + coordinate),
                new Vec3(originX + extent, 0, originZ + coordinate),
                color,
                thickness);
            Lines.Add(
                new Vec3(originX + coordinate, 0, originZ - extent),
                new Vec3(originX + coordinate, 0, originZ + extent),
                color,
                thickness);
        }

        Lines.Add(new Vec3(originX - extent, 0, 0), new Vec3(originX + extent, 0, 0), new Color32(190, 75, 75, 205), spacing * 0.015f);
        Lines.Add(new Vec3(0, 0, originZ - extent), new Vec3(0, 0, originZ + extent), new Color32(75, 115, 205, 205), spacing * 0.015f);
    }

    private bool UpdateAndDrawViewWidget()
    {
        const float widgetDepth = 0.38f;
        var cameraRotation = CameraRotation();
        var cameraPosition = CameraPosition();
        var viewportSize = _viewportMetrics.GetClientSize(
            SK.System.displayWidth,
            SK.System.displayHeight);
        var layout = SceneViewWidgetLayout.Calculate(
            viewportSize.Width,
            viewportSize.Height,
            _camera.Projection,
            Renderer.FOV,
            Renderer.OrthoSize,
            widgetDepth);
        var center = cameraPosition
            + ((cameraRotation * Vec3.Forward) * widgetDepth)
            + ((cameraRotation * Vec3.Right) * layout.HorizontalWorldOffset)
            + ((cameraRotation * Vec3.Up) * layout.VerticalWorldOffset);
        var directions = new[]
        {
            new ViewDirection(Vec3.UnitX, new Color(0.92f, 0.30f, 0.30f, 1), 90, 0),
            new ViewDirection(-Vec3.UnitX, new Color(0.54f, 0.20f, 0.20f, 1), -90, 0),
            new ViewDirection(Vec3.UnitY, new Color(0.32f, 0.86f, 0.42f, 1), 0, -89),
            new ViewDirection(-Vec3.UnitY, new Color(0.18f, 0.48f, 0.24f, 1), 0, 89),
            new ViewDirection(Vec3.UnitZ, new Color(0.26f, 0.48f, 0.94f, 1), 180, 0),
            new ViewDirection(-Vec3.UnitZ, new Color(0.16f, 0.28f, 0.58f, 1), 0, 0),
        };

        var ray = Input.Mouse.Ray;
        var hovered = -1;
        var bestDistance = layout.FaceSize * 1.2f;
        for (var index = 0; index < directions.Length; index++)
        {
            var endpoint = center + (directions[index].Axis * layout.ArmLength);
            var distance = DistanceRayToPoint(ray, endpoint);
            if (distance < bestDistance)
            {
                hovered = index;
                bestDistance = distance;
            }

            DrawViewWidgetArm(center, endpoint, directions[index].Color, layout.ArmThickness);
        }

        Mesh.Cube.Draw(
            _gizmoOverlayMaterial,
            Matrix.TRS(center, Quat.Identity, new Vec3(layout.CenterSize, layout.CenterSize, layout.CenterSize)),
            new Color(0.78f, 0.82f, 0.88f, 1));

        for (var index = 0; index < directions.Length; index++)
        {
            var direction = directions[index];
            var endpoint = center + (direction.Axis * layout.ArmLength);
            var color = index == hovered
                ? new Color(1.0f, 0.84f, 0.28f, 1)
                : direction.Color;
            var size = index == hovered ? layout.FaceSize * 1.25f : layout.FaceSize;
            Mesh.Cube.Draw(
                _gizmoOverlayMaterial,
                Matrix.TRS(endpoint, Quat.Identity, new Vec3(size, size, size)),
                color);
        }

        if (Input.Key(Key.MouseLeft).IsJustActive())
        {
            if (hovered >= 0)
            {
                var direction = directions[hovered];
                _camera = _camera with
                {
                    YawDegrees = direction.YawDegrees,
                    PitchDegrees = direction.PitchDegrees,
                };
                PublishCamera(force: true);
                return true;
            }

            if (DistanceRayToPoint(ray, center) < layout.CenterSize * 0.85f)
            {
                _camera = _camera with { YawDegrees = 45, PitchDegrees = 28 };
                PublishCamera(force: true);
                return true;
            }
        }

        return hovered >= 0;
    }

    private void DrawViewWidgetArm(Vec3 start, Vec3 end, Color color, float thickness)
    {
        var delta = end - start;
        var scale = new Vec3(
            MathF.Max(MathF.Abs(delta.x), thickness),
            MathF.Max(MathF.Abs(delta.y), thickness),
            MathF.Max(MathF.Abs(delta.z), thickness));
        Mesh.Cube.Draw(
            _gizmoOverlayMaterial,
            Matrix.TRS((start + end) * 0.5f, Quat.Identity, scale),
            color);
    }

    private static Material CreateGizmoOverlayMaterial()
    {
        var material = Material.Default.Copy();
        material.DepthTest = DepthTest.Always;
        material.DepthWrite = false;
        // Render after regular scene geometry and the sky without using an
        // extreme queue value that some backends may clamp or discard.
        material.QueueOffset = 100;
        return material;
    }

    private bool UpdateAndDrawGizmo(
        SceneDocument scene,
        Guid? selectedEntityId,
        IReadOnlyList<Guid> selectedEntityIds,
        long revision,
        bool allowInteraction)
    {
        if (!TryFindEntityTransform(scene, selectedEntityId, out var entity, out var parentWorld, out var world))
        {
            _drag = null;
            return false;
        }

        // Spatial UI elements have purpose-built layout/anchor handles. A
        // second world-transform gizmo at the same point is misleading and
        // visually obscures those controls.
        if (entity.Components.UiRect is not null)
        {
            _drag = null;
            return false;
        }

        var targets = CaptureTransformTargets(scene, selectedEntityId, selectedEntityIds);
        if (targets.Count == 0)
        {
            _drag = null;
            return false;
        }

        var gizmoOrigin = _settings.PivotMode == ScenePivotMode.Active || targets.Count == 1
            ? world.Translation
            : AverageWorldPosition(targets);

        if (_drag is { } staleDrag
            && (staleDrag.EntityId != entity.Id || staleDrag.Revision != revision))
        {
            _drag = null;
        }
        else if (_drag is { } switchedDrag && switchedDrag.Tool != _settings.Tool)
        {
            RestoreTargets(switchedDrag);
            _drag = null;
        }

        if (_drag is { } cancelDrag && Input.Key(Key.Esc).IsJustActive())
        {
            RestoreTargets(cancelDrag);
            _drag = null;
            DrawCurrentGizmo(world, -1);
            return true;
        }

        var pointerConsumed = _settings.Tool switch
        {
            SceneTransformTool.Rotate => UpdateAndDrawRotation(
                entity,
                parentWorld,
                world,
                gizmoOrigin,
                targets,
                revision,
                allowInteraction),
            SceneTransformTool.Scale => UpdateAndDrawScale(
                entity,
                parentWorld,
                world,
                gizmoOrigin,
                targets,
                revision,
                allowInteraction),
            _ => UpdateAndDrawMove(
                entity,
                parentWorld,
                world,
                gizmoOrigin,
                targets,
                revision,
                allowInteraction),
        };
        if (_drag is { NumericLabel.Length: > 0 } activeDrag)
        {
            DrawGestureOverlay(activeDrag);
        }

        return pointerConsumed;
    }

    private bool UpdateAndDrawMove(
        SceneEntity entity,
        Matrix parentWorld,
        Matrix world,
        Vec3 origin,
        IReadOnlyList<TransformTarget> targets,
        long revision,
        bool allowInteraction)
    {
        var axes = GetOrientedAxes(world);
        var length = GizmoLength(origin);
        var ray = Input.Mouse.Ray;
        var left = Input.Key(Key.MouseLeft);
        var hoveredAxis = _drag?.AxisIndex
            ?? (allowInteraction ? PickMoveHandle(ray, origin, axes, length) : -1);
        if (HandleDuplicateDrag(
                targets,
                revision,
                SceneTransformTool.Move,
                hoveredAxis,
                left,
                out var resumeMove,
                out var resumedMoveHandle))
        {
            DrawMoveAxes(origin, axes, length, hoveredAxis);
            return true;
        }

        if (resumeMove)
        {
            hoveredAxis = resumedMoveHandle;
        }

        var beginMove = left.IsJustActive() || resumeMove;

        if (_drag is null
            && allowInteraction
            && beginMove
            && hoveredAxis is >= 0 and < 3
            && TryCreateAxisConstraint(ray, origin, axes[hoveredAxis], out var planeNormal, out var coordinate))
        {
            _drag = new(
                SceneTransformTool.Move,
                entity.Id,
                revision,
                hoveredAxis,
                axes[hoveredAxis],
                planeNormal,
                origin,
                coordinate,
                Vec3.Zero,
                entity.Components.Transform,
                parentWorld,
                Input.Mouse.pos,
                length,
                targets);
        }
        else if (_drag is null
                 && allowInteraction
                 && beginMove
                 && hoveredAxis is >= 3 and <= 5)
        {
            var (first, second) = MovePlaneAxes(hoveredAxis);
            var normal = Vec3.Cross(axes[first], axes[second]).Normalized;
            if (TryRayPlaneIntersection(ray, origin, normal, out var initialHit))
            {
                _drag = new(
                    SceneTransformTool.Move,
                    entity.Id,
                    revision,
                    hoveredAxis,
                    axes[first],
                    normal,
                    origin,
                    0,
                    initialHit,
                    entity.Components.Transform,
                    parentWorld,
                    Input.Mouse.pos,
                    length,
                    targets);
            }
        }

        if (_drag is { Tool: SceneTransformTool.Move } drag)
        {
            hoveredAxis = drag.AxisIndex;
            if (left.IsActive()
                && TryRayPlaneIntersection(ray, drag.StartWorldOrigin, drag.PlaneNormal, out var hit))
            {
                Vec3 worldDelta;
                if (drag.AxisIndex < 3)
                {
                    var distance = Vec3.Dot(hit - drag.StartWorldOrigin, drag.Axis)
                        - drag.StartAxisCoordinate;
                    if (_settings.TranslationSnapEnabled)
                    {
                        var increment = (float)_settings.TranslationSnap;
                        distance = MathF.Round(distance / increment) * increment;
                    }

                    worldDelta = drag.Axis * distance;
                }
                else
                {
                    var (first, second) = MovePlaneAxes(drag.AxisIndex);
                    var orientedAxes = GetOrientedAxes(world);
                    var delta = hit - drag.LastDirection;
                    var firstDistance = Vec3.Dot(delta, orientedAxes[first]);
                    var secondDistance = Vec3.Dot(delta, orientedAxes[second]);
                    if (_settings.TranslationSnapEnabled)
                    {
                        var increment = (float)_settings.TranslationSnap;
                        firstDistance = MathF.Round(firstDistance / increment) * increment;
                        secondDistance = MathF.Round(secondDistance / increment) * increment;
                    }

                    worldDelta = (orientedAxes[first] * firstDistance)
                        + (orientedAxes[second] * secondDistance);
                    drag.NumericLabel = $"{firstDistance:+0.###;-0.###;0}, {secondDistance:+0.###;-0.###;0} m";
                }

                if (drag.AxisIndex < 3)
                {
                    drag.NumericLabel = $"{worldDelta.Length:+0.###;-0.###;0} m";
                }

                ApplyTranslation(drag, worldDelta);
            }

            CompleteDragOnRelease(left);
        }

        DrawMoveAxes(origin, axes, length, hoveredAxis);
        return _drag is not null || (allowInteraction && left.IsJustActive() && hoveredAxis >= 0);
    }

    private bool UpdateAndDrawRotation(
        SceneEntity entity,
        Matrix parentWorld,
        Matrix world,
        Vec3 origin,
        IReadOnlyList<TransformTarget> targets,
        long revision,
        bool allowInteraction)
    {
        var axes = GetOrientedAxes(world);
        var radius = GizmoLength(origin) * 0.82f;
        var ray = Input.Mouse.Ray;
        var left = Input.Key(Key.MouseLeft);
        var hoveredAxis = _drag?.AxisIndex ?? -1;
        if (_drag is null && allowInteraction)
        {
            hoveredAxis = DistanceRayToPoint(ray, origin) < radius * 0.18f
                ? 4
                : PickRotationAxis(ray, origin, axes, radius, radius * 0.11f);
            if (hoveredAxis < 0)
            {
                var screenAxis = CameraRotation() * Vec3.Forward;
                if (TryRayPlaneIntersection(ray, origin, screenAxis, out var screenHit)
                    && MathF.Abs(Vec3.Distance(screenHit, origin) - (radius * 1.18f)) < radius * 0.10f)
                {
                    hoveredAxis = 3;
                }
            }
        }

        if (HandleDuplicateDrag(
                targets,
                revision,
                SceneTransformTool.Rotate,
                hoveredAxis,
                left,
                out var resumeRotate,
                out var resumedRotateHandle))
        {
            DrawRotationRings(origin, axes, radius, hoveredAxis);
            DrawScreenRotationRing(origin, CameraRotation() * Vec3.Forward, radius * 1.18f, hoveredAxis == 3);
            DrawFreeRotationHandle(origin, CameraRotation(), radius, hoveredAxis == 4);
            return true;
        }

        if (resumeRotate)
        {
            hoveredAxis = resumedRotateHandle;
        }

        var beginRotate = left.IsJustActive() || resumeRotate;

        if (_drag is null
            && allowInteraction
            && beginRotate
            && hoveredAxis >= 0)
        {
            var rotationAxis = hoveredAxis == 3
                ? CameraRotation() * Vec3.Forward
                : hoveredAxis == 4 ? Vec3.Zero : axes[hoveredAxis];
            if (hoveredAxis == 4)
            {
                _drag = new(
                    SceneTransformTool.Rotate,
                    entity.Id,
                    revision,
                    hoveredAxis,
                    rotationAxis,
                    rotationAxis,
                    origin,
                    0,
                    Vec3.Zero,
                    entity.Components.Transform,
                    parentWorld,
                    Input.Mouse.pos,
                    radius,
                    targets);
            }
            else if (TryRayPlaneIntersection(ray, origin, rotationAxis, out var initialHit))
            {
                var startDirection = initialHit - origin;
                if (startDirection.LengthSq > 0.0001f)
                {
                    startDirection.Normalize();
                    _drag = new(
                        SceneTransformTool.Rotate,
                        entity.Id,
                        revision,
                        hoveredAxis,
                        rotationAxis,
                        rotationAxis,
                        origin,
                        0,
                        startDirection,
                        entity.Components.Transform,
                        parentWorld,
                        Input.Mouse.pos,
                        radius,
                        targets);
                }
            }
        }

        if (_drag is { Tool: SceneTransformTool.Rotate } drag)
        {
            hoveredAxis = drag.AxisIndex;
            if (left.IsActive() && drag.AxisIndex == 4)
            {
                var mouseDelta = Input.Mouse.pos - drag.StartMousePosition;
                var cameraRotation = CameraRotation();
                var yaw = mouseDelta.x * 0.25f;
                var pitch = -mouseDelta.y * 0.25f;
                var delta = AxisAngle(cameraRotation * Vec3.Up, yaw)
                    * AxisAngle(cameraRotation * Vec3.Right, pitch);
                ApplyRotationDelta(drag, delta);
                drag.NumericLabel = $"free {MathF.Sqrt((yaw * yaw) + (pitch * pitch)):0.##}°";
            }
            else if (left.IsActive()
                && TryRayPlaneIntersection(ray, drag.StartWorldOrigin, drag.Axis, out var hit))
            {
                var direction = hit - drag.StartWorldOrigin;
                if (direction.LengthSq > 0.0001f)
                {
                    direction.Normalize();
                    drag.AccumulatedAngleDegrees += SignedAngleDegrees(
                        drag.LastDirection,
                        direction,
                        drag.Axis);
                    drag.LastDirection = direction;
                    var angle = drag.AccumulatedAngleDegrees;
                    if (_settings.RotationSnapEnabled)
                    {
                        var increment = (float)_settings.RotationSnapDegrees;
                        angle = MathF.Round(angle / increment) * increment;
                    }

                    drag.NumericLabel = $"{angle:+0.##;-0.##;0}°";

                    ApplyRotation(drag, angle);
                }
            }

            CompleteDragOnRelease(left);
        }

        DrawRotationRings(origin, axes, radius, hoveredAxis);
        DrawScreenRotationRing(origin, CameraRotation() * Vec3.Forward, radius * 1.18f, hoveredAxis == 3);
        DrawFreeRotationHandle(origin, CameraRotation(), radius, hoveredAxis == 4);
        return _drag is not null || (allowInteraction && left.IsJustActive() && hoveredAxis >= 0);
    }

    private bool UpdateAndDrawScale(
        SceneEntity entity,
        Matrix parentWorld,
        Matrix world,
        Vec3 origin,
        IReadOnlyList<TransformTarget> targets,
        long revision,
        bool allowInteraction)
    {
        // A TRS scene format cannot represent the shear that global-axis scaling
        // can introduce, so Scale intentionally follows the object's local axes.
        var axes = GetLocalAxes(world);
        var length = GizmoLength(origin);
        var ray = Input.Mouse.Ray;
        var left = Input.Key(Key.MouseLeft);
        var hoveredAxis = _drag?.AxisIndex ?? -1;
        if (_drag is null && allowInteraction)
        {
            hoveredAxis = DistanceRayToPoint(ray, origin) < length * 0.11f
                ? 3
                : PickAxis(ray, origin, axes, length, length * 0.13f);
        }

        if (HandleDuplicateDrag(
                targets,
                revision,
                SceneTransformTool.Scale,
                hoveredAxis,
                left,
                out var resumeScale,
                out var resumedScaleHandle))
        {
            DrawScaleAxes(origin, axes, length, hoveredAxis);
            return true;
        }

        if (resumeScale)
        {
            hoveredAxis = resumedScaleHandle;
        }

        var beginScale = left.IsJustActive() || resumeScale;

        if (_drag is null && allowInteraction && beginScale && hoveredAxis >= 0)
        {
            if (hoveredAxis == 3)
            {
                _drag = new(
                    SceneTransformTool.Scale,
                    entity.Id,
                    revision,
                    hoveredAxis,
                    Vec3.Zero,
                    Vec3.Zero,
                    origin,
                    0,
                    Vec3.Zero,
                    entity.Components.Transform,
                    parentWorld,
                    Input.Mouse.pos,
                    length,
                    targets);
            }
            else if (TryCreateAxisConstraint(
                         ray,
                         origin,
                         axes[hoveredAxis],
                         out var planeNormal,
                         out var coordinate))
            {
                _drag = new(
                    SceneTransformTool.Scale,
                    entity.Id,
                    revision,
                    hoveredAxis,
                    axes[hoveredAxis],
                    planeNormal,
                    origin,
                    coordinate,
                    Vec3.Zero,
                    entity.Components.Transform,
                    parentWorld,
                    Input.Mouse.pos,
                    length,
                    targets);
            }
        }

        if (_drag is { Tool: SceneTransformTool.Scale } drag)
        {
            hoveredAxis = drag.AxisIndex;
            if (left.IsActive())
            {
                float factor;
                if (drag.AxisIndex == 3)
                {
                    factor = 1 + ((drag.StartMousePosition.y - Input.Mouse.pos.y) * 0.01f);
                }
                else if (TryRayPlaneIntersection(
                             ray,
                             drag.StartWorldOrigin,
                             drag.PlaneNormal,
                             out var hit))
                {
                    var distance = Vec3.Dot(hit - drag.StartWorldOrigin, drag.Axis)
                        - drag.StartAxisCoordinate;
                    factor = 1 + (distance / drag.GizmoSize);
                }
                else
                {
                    factor = 1;
                }

                ApplyScale(drag, MathF.Max(0.01f, factor));
                drag.NumericLabel = $"{MathF.Max(0.01f, factor):0.###}×";
            }

            CompleteDragOnRelease(left);
        }

        DrawScaleAxes(origin, axes, length, hoveredAxis);
        return _drag is not null || (allowInteraction && left.IsJustActive() && hoveredAxis >= 0);
    }

    private bool HandleDuplicateDrag(
        IReadOnlyList<TransformTarget> targets,
        long revision,
        SceneTransformTool tool,
        int hoveredHandle,
        BtnState left,
        out bool resume,
        out int resumedHandle)
    {
        resume = false;
        resumedHandle = -1;
        if (_drag is not null)
        {
            return false;
        }

        if (_pendingDuplicateDrag is { } pending)
        {
            if (!left.IsActive() || pending.Tool != tool)
            {
                _pendingDuplicateDrag = null;
                return false;
            }

            var currentIds = targets.Select(target => target.Entity.Id).ToHashSet();
            if (revision > pending.Revision && !currentIds.SetEquals(pending.OriginalEntityIds))
            {
                resume = true;
                resumedHandle = pending.Handle;
                _pendingDuplicateDrag = null;
                return false;
            }

            return true;
        }

        if (hoveredHandle < 0
            || !left.IsJustActive()
            || !Input.Key(Key.Ctrl).IsActive())
        {
            return false;
        }

        var entityIds = targets.Select(target => target.Entity.Id).Distinct().ToArray();
        if (entityIds.Length == 0)
        {
            return false;
        }

        _pendingDuplicateDrag = new(tool, hoveredHandle, revision, entityIds.ToHashSet());
        duplicateSelectionRequested(entityIds);
        return true;
    }

    private void CompleteDragOnRelease(BtnState left)
    {
        if (_drag is null || !left.IsJustInactive())
        {
            return;
        }

        var committed = _drag.Targets
            .Select(target => new EntityTransformValue(target.Entity.Id, target.Entity.Components.Transform))
            .ToArray();
        if (committed.Length == 1)
        {
            transformCommitted(committed[0].EntityId, committed[0].Transform);
        }
        else
        {
            transformsCommitted(committed);
        }

        _drag = null;
    }

    private void DrawGestureOverlay(GizmoDrag drag)
    {
        var cameraRotation = CameraRotation();
        var position = drag.StartWorldOrigin
            + ((cameraRotation * Vec3.Up) * drag.GizmoSize * 0.30f)
            + ((cameraRotation * Vec3.Right) * drag.GizmoSize * 0.15f);
        Text.Add(
            drag.NumericLabel,
            Matrix.TRS(position, cameraRotation, new Vec3(0.45f, 0.45f, 0.45f)),
            TextStyle.Default,
            new Color(1.0f, 0.86f, 0.35f, 1.0f));
    }

    private static void ApplyTranslation(GizmoDrag drag, Vec3 worldDelta)
    {
        foreach (var target in drag.Targets)
        {
            var localDelta = target.ParentWorld.Inverse.TransformNormal(worldDelta);
            target.Entity.Components.Transform = target.StartTransform with
            {
                Position = new Vector3Value(
                    target.StartTransform.Position.X + localDelta.x,
                    target.StartTransform.Position.Y + localDelta.y,
                    target.StartTransform.Position.Z + localDelta.z),
            };
        }
    }

    private static void ApplyRotation(GizmoDrag drag, float angleDegrees)
    {
        ApplyRotationDelta(drag, AxisAngle(drag.Axis, angleDegrees));
    }

    private static void ApplyRotationDelta(GizmoDrag drag, Quat delta)
    {
        foreach (var target in drag.Targets)
        {
            var startWorldPosition = target.World.Translation;
            var nextWorldPosition = drag.StartWorldOrigin
                + (delta * (startWorldPosition - drag.StartWorldOrigin));
            var nextLocalPosition = target.ParentWorld.Inverse.Transform(nextWorldPosition);
            var nextWorldRotation = delta * target.World.Rotation;
            var nextLocalRotation = target.ParentWorld.Rotation.Inverse * nextWorldRotation;
            nextLocalRotation.Normalize();
            target.Entity.Components.Transform = target.StartTransform with
            {
                Position = FromVec3(nextLocalPosition),
                Rotation = FromQuat(nextLocalRotation),
            };
        }
    }

    private void ApplyScale(GizmoDrag drag, float factor)
    {
        foreach (var target in drag.Targets)
        {
            var start = target.StartTransform.Scale;
            Vector3Value next;
            if (drag.AxisIndex == 3)
            {
                next = new(
                    ScaleValue(start.X * factor),
                    ScaleValue(start.Y * factor),
                    ScaleValue(start.Z * factor));
            }
            else
            {
                var value = drag.AxisIndex switch
                {
                    0 => ScaleValue(start.X * factor),
                    1 => ScaleValue(start.Y * factor),
                    _ => ScaleValue(start.Z * factor),
                };
                next = drag.AxisIndex switch
                {
                    0 => start with { X = value },
                    1 => start with { Y = value },
                    _ => start with { Z = value },
                };
            }

            var offset = target.World.Translation - drag.StartWorldOrigin;
            Vec3 nextOffset;
            if (drag.AxisIndex == 3)
            {
                nextOffset = offset * factor;
            }
            else
            {
                var alongAxis = drag.Axis * Vec3.Dot(offset, drag.Axis);
                nextOffset = offset + (alongAxis * (factor - 1));
            }

            var nextLocalPosition = target.ParentWorld.Inverse.Transform(drag.StartWorldOrigin + nextOffset);
            target.Entity.Components.Transform = target.StartTransform with
            {
                Position = FromVec3(nextLocalPosition),
                Scale = next,
            };
        }
    }

    private static void RestoreTargets(GizmoDrag drag)
    {
        foreach (var target in drag.Targets)
        {
            target.Entity.Components.Transform = target.StartTransform;
        }
    }

    private double ScaleValue(double value)
    {
        value = Math.Max(0.01, value);
        if (_settings.ScaleSnapEnabled)
        {
            value = Math.Round(value / _settings.ScaleSnap) * _settings.ScaleSnap;
        }

        return Math.Max(0.01, value);
    }

    private void DrawCurrentGizmo(Matrix world, int highlightedAxis)
    {
        var origin = world.Translation;
        if (_settings.Tool == SceneTransformTool.Rotate)
        {
            var radius = GizmoLength(origin) * 0.82f;
            DrawRotationRings(origin, GetOrientedAxes(world), radius, highlightedAxis);
            DrawScreenRotationRing(origin, CameraRotation() * Vec3.Forward, radius * 1.18f, highlightedAxis == 3);
            DrawFreeRotationHandle(origin, CameraRotation(), radius, highlightedAxis == 4);
        }
        else if (_settings.Tool == SceneTransformTool.Scale)
        {
            DrawScaleAxes(origin, GetLocalAxes(world), GizmoLength(origin), highlightedAxis);
        }
        else
        {
            DrawMoveAxes(origin, GetOrientedAxes(world), GizmoLength(origin), highlightedAxis);
        }
    }

    private float GizmoLength(Vec3 origin) =>
        Math.Clamp(Vec3.Distance(CameraPosition(), origin) * 0.18f, 0.09f, 0.45f);

    private Vec3[] GetOrientedAxes(Matrix world) =>
        _settings.GizmoSpace == SceneGizmoSpace.Global
            ? [Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ]
            : GetLocalAxes(world);

    private static Vec3[] GetLocalAxes(Matrix world) =>
    [
        world.TransformNormal(Vec3.UnitX).Normalized,
        world.TransformNormal(Vec3.UnitY).Normalized,
        world.TransformNormal(Vec3.UnitZ).Normalized,
    ];

    private static bool TryCreateAxisConstraint(
        Ray ray,
        Vec3 origin,
        Vec3 axis,
        out Vec3 planeNormal,
        out float coordinate)
    {
        var viewDirection = (origin - ray.position).Normalized;
        planeNormal = viewDirection - (axis * Vec3.Dot(viewDirection, axis));
        if (planeNormal.LengthSq < 0.0001f)
        {
            planeNormal = Vec3.Cross(axis, Vec3.Up);
            if (planeNormal.LengthSq < 0.0001f)
            {
                planeNormal = Vec3.Cross(axis, Vec3.Right);
            }
        }

        planeNormal.Normalize();
        if (TryRayPlaneIntersection(ray, origin, planeNormal, out var hit))
        {
            coordinate = Vec3.Dot(hit - origin, axis);
            return true;
        }

        coordinate = 0;
        return false;
    }

    private static int PickAxis(Ray ray, Vec3 origin, IReadOnlyList<Vec3> axes, float length, float radius)
    {
        var bestAxis = -1;
        var bestDistance = radius;
        for (var index = 0; index < axes.Count; index++)
        {
            var distance = DistanceRayToSegment(ray, origin, origin + (axes[index] * length));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestAxis = index;
            }
        }

        return bestAxis;
    }

    private static int PickMoveHandle(Ray ray, Vec3 origin, IReadOnlyList<Vec3> axes, float length)
    {
        var axis = PickAxis(ray, origin, axes, length, length * 0.13f);
        if (axis >= 0)
        {
            return axis;
        }

        const float innerFactor = 0.20f;
        const float outerFactor = 0.38f;
        for (var handle = 3; handle <= 5; handle++)
        {
            var (first, second) = MovePlaneAxes(handle);
            var normal = Vec3.Cross(axes[first], axes[second]).Normalized;
            if (!TryRayPlaneIntersection(ray, origin, normal, out var hit))
            {
                continue;
            }

            var offset = hit - origin;
            var firstDistance = Vec3.Dot(offset, axes[first]) / length;
            var secondDistance = Vec3.Dot(offset, axes[second]) / length;
            if (firstDistance >= innerFactor && firstDistance <= outerFactor
                && secondDistance >= innerFactor && secondDistance <= outerFactor)
            {
                return handle;
            }
        }

        return -1;
    }

    private static (int First, int Second) MovePlaneAxes(int handle) => handle switch
    {
        3 => (0, 1),
        4 => (0, 2),
        _ => (1, 2),
    };

    private static int PickRotationAxis(
        Ray ray,
        Vec3 origin,
        IReadOnlyList<Vec3> axes,
        float radius,
        float tolerance)
    {
        var bestAxis = -1;
        var bestError = tolerance;
        for (var index = 0; index < axes.Count; index++)
        {
            if (!TryRayPlaneIntersection(ray, origin, axes[index], out var hit))
            {
                continue;
            }

            var error = MathF.Abs(Vec3.Distance(hit, origin) - radius);
            if (error < bestError)
            {
                bestError = error;
                bestAxis = index;
            }
        }

        return bestAxis;
    }

    private static float DistanceRayToSegment(Ray ray, Vec3 start, Vec3 end)
    {
        var segment = end - start;
        var length = segment.Length;
        if (length < 0.00001f)
        {
            return Vec3.Distance(ray.position, start);
        }

        var axis = segment / length;
        var between = ray.position - start;
        var dot = Vec3.Dot(ray.direction, axis);
        var denominator = 1 - (dot * dot);
        var segmentDistance = MathF.Abs(denominator) < 0.0001f
            ? Vec3.Dot(between, axis)
            : (Vec3.Dot(axis, between) - (dot * Vec3.Dot(ray.direction, between))) / denominator;
        segmentDistance = Math.Clamp(segmentDistance, 0, length);
        var segmentPoint = start + (axis * segmentDistance);
        var rayDistance = MathF.Max(0, Vec3.Dot(segmentPoint - ray.position, ray.direction));
        var rayPoint = ray.position + (ray.direction * rayDistance);
        return Vec3.Distance(rayPoint, segmentPoint);
    }

    private static float DistanceRayToPoint(Ray ray, Vec3 point)
    {
        var distanceAlongRay = MathF.Max(0, Vec3.Dot(point - ray.position, ray.direction));
        return Vec3.Distance(ray.position + (ray.direction * distanceAlongRay), point);
    }

    private static bool TryRayPlaneIntersection(Ray ray, Vec3 point, Vec3 normal, out Vec3 hit)
    {
        var denominator = Vec3.Dot(ray.direction, normal);
        if (MathF.Abs(denominator) < 0.00001f)
        {
            hit = Vec3.Zero;
            return false;
        }

        var distance = Vec3.Dot(point - ray.position, normal) / denominator;
        if (distance < 0)
        {
            hit = Vec3.Zero;
            return false;
        }

        hit = ray.position + (ray.direction * distance);
        return true;
    }

    private void DrawMoveAxes(
        Vec3 origin,
        IReadOnlyList<Vec3> axes,
        float length,
        int highlightedAxis)
    {
        var colors = AxisColors();
        var highlight = HighlightColor();
        for (var index = 0; index < axes.Count; index++)
        {
            var color = index == highlightedAxis ? highlight : colors[index];
            var end = origin + (axes[index] * length);
            DrawOverlaySegment(
                origin,
                end,
                color,
                length * (index == highlightedAxis ? 0.035f : 0.022f));
            DrawOverlaySegment(
                end - (axes[index] * length * 0.08f),
                end,
                color,
                length * 0.075f);
        }


        for (var handle = 3; handle <= 5; handle++)
        {
            var (first, second) = MovePlaneAxes(handle);
            var firstAxis = axes[first];
            var secondAxis = axes[second];
            var inner = length * 0.20f;
            var outer = length * 0.38f;
            var a = origin + (firstAxis * inner) + (secondAxis * inner);
            var b = origin + (firstAxis * outer) + (secondAxis * inner);
            var c = origin + (firstAxis * outer) + (secondAxis * outer);
            var d = origin + (firstAxis * inner) + (secondAxis * outer);
            var color = handle == highlightedAxis
                ? highlight
                : BlendAxisColors(colors[first], colors[second]);
            var thickness = length * (handle == highlightedAxis ? 0.027f : 0.014f);
            DrawOverlaySegment(a, b, color, thickness);
            DrawOverlaySegment(b, c, color, thickness);
            DrawOverlaySegment(c, d, color, thickness);
            DrawOverlaySegment(d, a, color, thickness);
        }
    }

    private static Color BlendAxisColors(Color first, Color second) => new(
        (first.r + second.r) * 0.5f,
        (first.g + second.g) * 0.5f,
        (first.b + second.b) * 0.5f,
        0.88f);

    private void DrawScaleAxes(
        Vec3 origin,
        IReadOnlyList<Vec3> axes,
        float length,
        int highlightedAxis)
    {
        var colors = AxisColors();
        var highlight = HighlightColor();
        for (var index = 0; index < axes.Count; index++)
        {
            var color = index == highlightedAxis ? highlight : colors[index];
            var end = origin + (axes[index] * length);
            var perpendicular = Vec3.Cross(axes[index], Vec3.Up);
            if (perpendicular.LengthSq < 0.0001f)
            {
                perpendicular = Vec3.Cross(axes[index], Vec3.Right);
            }

            perpendicular.Normalize();
            DrawOverlaySegment(
                origin,
                end,
                color,
                length * (index == highlightedAxis ? 0.035f : 0.022f));
            DrawOverlaySegment(
                end - (perpendicular * length * 0.045f),
                end + (perpendicular * length * 0.045f),
                color,
                length * 0.045f);
        }

        var centerColor = highlightedAxis == 3 ? highlight : new Color(0.90f, 0.90f, 0.90f, 1);
        var centerSize = length * 0.055f;
        DrawOverlaySegment(
            origin - (Vec3.Right * centerSize),
            origin + (Vec3.Right * centerSize),
            centerColor,
            centerSize);
        DrawOverlaySegment(
            origin - (Vec3.Up * centerSize),
            origin + (Vec3.Up * centerSize),
            centerColor,
            centerSize);
    }

    private void DrawRotationRings(
        Vec3 origin,
        IReadOnlyList<Vec3> axes,
        float radius,
        int highlightedAxis)
    {
        const int segments = 64;
        var colors = AxisColors();
        var highlight = HighlightColor();
        for (var axisIndex = 0; axisIndex < axes.Count; axisIndex++)
        {
            var axis = axes[axisIndex];
            var tangent = Vec3.Cross(axis, MathF.Abs(Vec3.Dot(axis, Vec3.Up)) > 0.9f
                ? Vec3.Right
                : Vec3.Up).Normalized;
            var bitangent = Vec3.Cross(axis, tangent).Normalized;
            var color = axisIndex == highlightedAxis ? highlight : colors[axisIndex];
            var previous = origin + (tangent * radius);
            for (var segment = 1; segment <= segments; segment++)
            {
                var angle = MathF.PI * 2 * segment / segments;
                var next = origin + ((tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) * radius);
                DrawOverlaySegment(
                    previous,
                    next,
                    color,
                    radius * (axisIndex == highlightedAxis ? 0.025f : 0.015f));
                previous = next;
            }
        }
    }

    private void DrawScreenRotationRing(Vec3 origin, Vec3 normal, float radius, bool highlighted)
    {
        const int segments = 72;
        normal.Normalize();
        var tangent = Vec3.Cross(normal, MathF.Abs(Vec3.Dot(normal, Vec3.Up)) > 0.9f
            ? Vec3.Right
            : Vec3.Up).Normalized;
        var bitangent = Vec3.Cross(normal, tangent).Normalized;
        var color = highlighted ? HighlightColor() : new Color(0.80f, 0.83f, 0.86f, 0.80f);
        var previous = origin + (tangent * radius);
        for (var segment = 1; segment <= segments; segment++)
        {
            var angle = MathF.PI * 2 * segment / segments;
            var next = origin + ((tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) * radius);
            DrawOverlaySegment(
                previous,
                next,
                color,
                radius * (highlighted ? 0.021f : 0.012f));
            previous = next;
        }
    }

    private void DrawFreeRotationHandle(Vec3 origin, Quat cameraRotation, float radius, bool highlighted)
    {
        var color = highlighted ? HighlightColor() : new Color(0.84f, 0.86f, 0.89f, 0.82f);
        var size = radius * 0.12f;
        var right = cameraRotation * Vec3.Right;
        var up = cameraRotation * Vec3.Up;
        var thickness = radius * (highlighted ? 0.035f : 0.022f);
        DrawOverlaySegment(origin - (right * size), origin + (right * size), color, thickness);
        DrawOverlaySegment(origin - (up * size), origin + (up * size), color, thickness);
    }

    private void DrawOverlaySegment(Vec3 start, Vec3 end, Color color, float thickness)
    {
        var delta = end - start;
        var length = delta.Length;
        if (length < 0.00001f)
        {
            return;
        }

        var direction = delta / length;
        var up = MathF.Abs(Vec3.Dot(direction, Vec3.Up)) > 0.98f
            ? Vec3.Right
            : Vec3.Up;
        Mesh.Cube.Draw(
            _gizmoOverlayMaterial,
            Matrix.TRS(
                (start + end) * 0.5f,
                Quat.LookAt(Vec3.Zero, direction, up),
                new Vec3(thickness, thickness, length)),
            color);
    }

    private static Color[] AxisColors() =>
    [
        new Color(0.92f, 0.30f, 0.30f, 1),
        new Color(0.32f, 0.82f, 0.41f, 1),
        new Color(0.27f, 0.57f, 0.94f, 1),
    ];

    private static Color HighlightColor() => new(1.0f, 0.84f, 0.32f, 1);

    private void FrameEntity(SceneDocument scene, Guid? entityId)
    {
        if (!TryFindEntityTransform(scene, entityId, out var entity, out _, out var world))
        {
            return;
        }

        EditorPickBounds? bounds;
        var distanceMultiplier = 2.1;
        if (entity.Components.UiRect is not null
            && entityId is { } uiEntityId
            && TryResolveUiFrameTarget(scene, uiEntityId, out var panelWorld, out var uiFrameBounds))
        {
            // UI children are positioned by the panel layout engine rather than
            // by their authored Transform. Frame the element's calculated center
            // while keeping its owning panel in view.
            world = panelWorld;
            bounds = uiFrameBounds;
            distanceMultiplier = 1.25;
        }
        else
        {
            bounds = localBounds(entity);
        }

        var worldScale = world.Scale;
        var largestWorldScale = MathF.Max(
            MathF.Abs(worldScale.x),
            MathF.Max(MathF.Abs(worldScale.y), MathF.Abs(worldScale.z)));
        var pivot = bounds is { } local
            ? world.Transform(new Vec3((float)local.CenterX, (float)local.CenterY, (float)local.CenterZ))
            : world.Translation;
        var largestSize = bounds is { } measured
            ? Math.Max(measured.SizeX, Math.Max(measured.SizeY, measured.SizeZ))
            : 1;
        _camera = _camera with
        {
            Pivot = FromVec3(pivot),
            Distance = Math.Clamp(
                Math.Max(0.35, largestWorldScale * largestSize * distanceMultiplier),
                0.05,
                100),
        };
        PublishCamera(force: true);
    }

    internal static bool TryResolveUiFrameTarget(
        SceneDocument scene,
        Guid entityId,
        out Matrix panelWorld,
        out EditorPickBounds bounds)
    {
        foreach (var root in scene.Roots)
        {
            if (TryResolveUiFrameTarget(root, entityId, Matrix.Identity, out panelWorld, out bounds))
            {
                return true;
            }
        }

        panelWorld = Matrix.Identity;
        bounds = default;
        return false;
    }

    private static bool TryResolveUiFrameTarget(
        SceneEntity candidate,
        Guid entityId,
        Matrix parentWorld,
        out Matrix panelWorld,
        out EditorPickBounds bounds)
    {
        var transform = candidate.Components.Transform;
        var world = Matrix.TRS(
            ToVec3(transform.Position),
            ToQuat(transform.Rotation),
            ToVec3(transform.Scale)) * parentWorld;
        if (candidate.Components.UiPanel is { Visible: true } panel)
        {
            var panelSize = new Vector2Value(
                Math.Max(0.08, panel.Size.X),
                Math.Max(0.06, panel.Size.Y));
            var layout = SpatialUiLayoutEngine.Calculate(candidate, panelSize)
                .FirstOrDefault(value => value.Entity.Id == entityId);
            if (layout is not null)
            {
                panelWorld = world;
                bounds = new EditorPickBounds(
                    layout.Center.x,
                    layout.Center.y,
                    layout.Center.z,
                    panelSize.X,
                    panelSize.Y,
                    0.012);
                return true;
            }
        }

        foreach (var child in candidate.Children)
        {
            if (TryResolveUiFrameTarget(child, entityId, world, out panelWorld, out bounds))
            {
                return true;
            }
        }

        panelWorld = Matrix.Identity;
        bounds = default;
        return false;
    }

    private static IReadOnlyList<TransformTarget> CaptureTransformTargets(
        SceneDocument scene,
        Guid? activeEntityId,
        IReadOnlyList<Guid> selectedEntityIds)
    {
        var selected = selectedEntityIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (activeEntityId is { } active)
        {
            selected.Add(active);
        }

        var targets = new List<TransformTarget>();
        foreach (var root in scene.Roots)
        {
            Capture(root, Matrix.Identity, ancestorSelected: false);
        }

        return targets;

        void Capture(SceneEntity entity, Matrix parentWorld, bool ancestorSelected)
        {
            var transform = entity.Components.Transform;
            var world = Matrix.TRS(
                ToVec3(transform.Position),
                ToQuat(transform.Rotation),
                ToVec3(transform.Scale)) * parentWorld;
            var isSelected = selected.Contains(entity.Id);
            if (isSelected && !ancestorSelected)
            {
                targets.Add(new(entity, parentWorld, world, transform));
            }

            foreach (var child in entity.Children)
            {
                Capture(child, world, ancestorSelected || isSelected);
            }
        }
    }

    private static Vec3 AverageWorldPosition(IReadOnlyList<TransformTarget> targets)
    {
        var total = Vec3.Zero;
        foreach (var target in targets)
        {
            total += target.World.Translation;
        }

        return total / targets.Count;
    }

    private static bool TryFindEntityTransform(
        SceneDocument scene,
        Guid? entityId,
        out SceneEntity entity,
        out Matrix parentWorld,
        out Matrix world)
    {
        if (entityId is { } id)
        {
            foreach (var root in scene.Roots)
            {
                if (TryFindEntityTransform(root, id, Matrix.Identity, out entity, out parentWorld, out world))
                {
                    return true;
                }
            }
        }

        entity = null!;
        parentWorld = Matrix.Identity;
        world = Matrix.Identity;
        return false;
    }

    private static bool TryFindEntityTransform(
        SceneEntity candidate,
        Guid entityId,
        Matrix candidateParentWorld,
        out SceneEntity entity,
        out Matrix parentWorld,
        out Matrix world)
    {
        var transform = candidate.Components.Transform;
        var local = Matrix.TRS(
            ToVec3(transform.Position),
            ToQuat(transform.Rotation),
            ToVec3(transform.Scale));
        var candidateWorld = local * candidateParentWorld;
        if (candidate.Id == entityId)
        {
            entity = candidate;
            parentWorld = candidateParentWorld;
            world = candidateWorld;
            return true;
        }

        foreach (var child in candidate.Children)
        {
            if (TryFindEntityTransform(child, entityId, candidateWorld, out entity, out parentWorld, out world))
            {
                return true;
            }
        }

        entity = null!;
        parentWorld = Matrix.Identity;
        world = Matrix.Identity;
        return false;
    }

    private Quat CameraRotation() => Quat.FromAngles(
        (float)_camera.PitchDegrees,
        (float)_camera.YawDegrees,
        0);

    private Vec3 CameraPosition() =>
        ToVec3(_camera.Pivot) - (CameraRotation() * Vec3.Forward * (float)_camera.Distance);

    private void PublishCamera(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastCameraSentTimestamp < Stopwatch.Frequency / 10)
        {
            return;
        }

        _lastCameraSentTimestamp = now;
        cameraChanged(_camera);
    }

    private static SceneCameraState Sanitize(SceneCameraState camera) => camera with
    {
        Distance = Math.Clamp(double.IsFinite(camera.Distance) ? camera.Distance : 0.75, 0.05, 100),
        YawDegrees = double.IsFinite(camera.YawDegrees) ? camera.YawDegrees % 360 : 0,
        PitchDegrees = Math.Clamp(double.IsFinite(camera.PitchDegrees) ? camera.PitchDegrees : 0, -89, 89),
        Projection = Enum.IsDefined(camera.Projection) ? camera.Projection : SceneProjection.Perspective,
        Pivot = IsFinite(camera.Pivot) ? camera.Pivot : SceneCameraState.Default.Pivot,
    };

    private static SceneToolSettings Sanitize(SceneToolSettings settings) => settings with
    {
        Tool = Enum.IsDefined(settings.Tool) ? settings.Tool : SceneTransformTool.Move,
        GizmoSpace = Enum.IsDefined(settings.GizmoSpace) ? settings.GizmoSpace : SceneGizmoSpace.Global,
        PivotMode = Enum.IsDefined(settings.PivotMode) ? settings.PivotMode : ScenePivotMode.Center,
        TranslationSnap = Math.Clamp(
            double.IsFinite(settings.TranslationSnap) ? settings.TranslationSnap : 0.05,
            0.001,
            10),
        RotationSnapDegrees = Math.Clamp(
            double.IsFinite(settings.RotationSnapDegrees) ? settings.RotationSnapDegrees : 15,
            0.001,
            180),
        ScaleSnap = Math.Clamp(
            double.IsFinite(settings.ScaleSnap) ? settings.ScaleSnap : 0.1,
            0.001,
            10),
    };

    private static float SignedAngleDegrees(Vec3 from, Vec3 to, Vec3 axis) =>
        MathF.Atan2(Vec3.Dot(axis, Vec3.Cross(from, to)), Vec3.Dot(from, to))
        * (180 / MathF.PI);

    private static Quat AxisAngle(Vec3 axis, float angleDegrees)
    {
        axis.Normalize();
        System.Numerics.Quaternion result = System.Numerics.Quaternion.CreateFromAxisAngle(
            new System.Numerics.Vector3(axis.x, axis.y, axis.z),
            angleDegrees * (MathF.PI / 180));
        return result;
    }

    private static bool IsFinite(Vector3Value value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static Vec3 ToVec3(Vector3Value value) => new((float)value.X, (float)value.Y, (float)value.Z);
    private static Vector3Value FromVec3(Vec3 value) => new(value.x, value.y, value.z);
    private static Quat ToQuat(QuaternionValue value) => new((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
    private static QuaternionValue FromQuat(Quat value) => new(value.x, value.y, value.z, value.w);

    private sealed record TransformTarget(
        SceneEntity Entity,
        Matrix ParentWorld,
        Matrix World,
        TransformComponent StartTransform);

    private sealed record ViewDirection(
        Vec3 Axis,
        Color Color,
        double YawDegrees,
        double PitchDegrees);

    private sealed record PendingDuplicateDrag(
        SceneTransformTool Tool,
        int Handle,
        long Revision,
        IReadOnlySet<Guid> OriginalEntityIds);

    private sealed class GizmoDrag(
        SceneTransformTool tool,
        Guid entityId,
        long revision,
        int axisIndex,
        Vec3 axis,
        Vec3 planeNormal,
        Vec3 startWorldOrigin,
        float startAxisCoordinate,
        Vec3 startDirection,
        TransformComponent startTransform,
        Matrix parentWorld,
        Vec2 startMousePosition,
        float gizmoSize,
        IReadOnlyList<TransformTarget> targets)
    {
        public SceneTransformTool Tool { get; } = tool;
        public Guid EntityId { get; } = entityId;
        public long Revision { get; } = revision;
        public int AxisIndex { get; } = axisIndex;
        public Vec3 Axis { get; } = axis;
        public Vec3 PlaneNormal { get; } = planeNormal;
        public Vec3 StartWorldOrigin { get; } = startWorldOrigin;
        public float StartAxisCoordinate { get; } = startAxisCoordinate;
        public Vec3 LastDirection { get; set; } = startDirection;
        public float AccumulatedAngleDegrees { get; set; }
        public TransformComponent StartTransform { get; } = startTransform;
        public Matrix ParentWorld { get; } = parentWorld;
        public Vec2 StartMousePosition { get; } = startMousePosition;
        public float GizmoSize { get; } = gizmoSize;
        public IReadOnlyList<TransformTarget> Targets { get; } = targets;
        public string NumericLabel { get; set; } = string.Empty;
    }
}
