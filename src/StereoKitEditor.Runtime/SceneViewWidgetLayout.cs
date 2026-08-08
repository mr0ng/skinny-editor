using StereoKitEditor.Protocol;

namespace StereoKitEditor.Runtime;

internal readonly record struct SceneViewWidgetLayout(
    float HorizontalWorldOffset,
    float VerticalWorldOffset,
    float WorldPerPixel,
    float ArmLength,
    float ArmThickness,
    float FaceSize,
    float CenterSize,
    float RadiusPixels,
    float MarginPixels,
    float EdgeInsetPixels)
{
    public static SceneViewWidgetLayout Calculate(
        int widthPixels,
        int heightPixels,
        SceneProjection projection,
        float verticalFieldOfViewDegrees,
        float orthographicHeight,
        float depth)
    {
        widthPixels = Math.Max(1, widthPixels);
        heightPixels = Math.Max(1, heightPixels);
        var minimumDimension = Math.Min(widthPixels, heightPixels);
        // Leave enough breathing room for the projected end cubes, including
        // foreshortening at oblique camera angles and high-DPI child windows.
        var marginPixels = Math.Clamp(minimumDimension * 0.025f, 12f, 24f);
        var desiredRadiusPixels = Math.Clamp(minimumDimension * 0.06f, 28f, 48f);
        var availableRadiusPixels = Math.Max(1f, (minimumDimension * 0.5f) - marginPixels);
        var radiusPixels = Math.Min(desiredRadiusPixels, availableRadiusPixels);
        depth = float.IsFinite(depth) ? Math.Max(0.05f, depth) : 0.38f;

        float verticalWorldSpan;
        if (projection == SceneProjection.Orthographic)
        {
            verticalWorldSpan = float.IsFinite(orthographicHeight)
                ? Math.Clamp(orthographicHeight, 0.05f, 100f)
                : 1f;
        }
        else
        {
            var fov = float.IsFinite(verticalFieldOfViewDegrees)
                ? Math.Clamp(verticalFieldOfViewDegrees, 1f, 175f)
                : 90f;
            verticalWorldSpan = 2f * depth * MathF.Tan(fov * MathF.PI / 360f);
        }

        var worldPerPixel = verticalWorldSpan / heightPixels;
        var halfHeight = verticalWorldSpan * 0.5f;
        var halfWidth = halfHeight * widthPixels / heightPixels;
        // World-space axes can project larger than their nominal pixel radius
        // when one endpoint points toward the camera. Reserve that projected
        // footprint so no endpoint is clipped at oblique view angles.
        var edgeInsetPixels = Math.Min(
            minimumDimension * 0.5f,
            marginPixels + (radiusPixels * 1.6f));
        var edgeInset = edgeInsetPixels * worldPerPixel;
        var horizontalOffset = Math.Max(0, halfWidth - edgeInset);
        var verticalOffset = Math.Max(0, halfHeight - edgeInset);
        var armLength = radiusPixels * 0.82f * worldPerPixel;
        var faceSize = radiusPixels * 0.36f * worldPerPixel;

        return new SceneViewWidgetLayout(
            horizontalOffset,
            verticalOffset,
            worldPerPixel,
            armLength,
            faceSize * 0.16f,
            faceSize,
            radiusPixels * 0.45f * worldPerPixel,
            radiusPixels,
            marginPixels,
            edgeInsetPixels);
    }
}
