using StereoKit;

namespace StereoKitEditor.Runtime;

internal sealed class SelectionOutlineRenderer
{
    internal const float ThicknessMeters = 0.002f;

    private static readonly Color OutlineColor = new(1.0f, 0.64f, 0.12f, 1);
    private static readonly Color32 OutlineLineColor = new(255, 163, 31, 255);
    private readonly Material _material = CreateMaterial();

    public void DrawSilhouette(Mesh mesh, Matrix transform, Vec3 sourceCenter, float renderedLargestDimension) =>
        mesh.Draw(
            _material,
            ExpandAround(transform, sourceCenter, CalculateExpansionScale(renderedLargestDimension)),
            OutlineColor,
            RenderLayer.Layer0);

    public void DrawSilhouette(Model model, Matrix transform, Vec3 sourceCenter, float renderedLargestDimension) =>
        model.Draw(
            _material,
            ExpandAround(transform, sourceCenter, CalculateExpansionScale(renderedLargestDimension)),
            OutlineColor,
            RenderLayer.Layer0);

    public void DrawPlanarBounds(Matrix localTransform, Vec2 size)
    {
        var halfWidth = (MathF.Max(0.001f, size.x) * 0.5f) + ThicknessMeters;
        var halfHeight = (MathF.Max(0.001f, size.y) * 0.5f) + ThicknessMeters;
        var depth = -ThicknessMeters * 2;
        var topLeft = localTransform.Transform(new Vec3(-halfWidth, halfHeight, depth));
        var topRight = localTransform.Transform(new Vec3(halfWidth, halfHeight, depth));
        var bottomRight = localTransform.Transform(new Vec3(halfWidth, -halfHeight, depth));
        var bottomLeft = localTransform.Transform(new Vec3(-halfWidth, -halfHeight, depth));

        Lines.Add(topLeft, topRight, OutlineLineColor, ThicknessMeters);
        Lines.Add(topRight, bottomRight, OutlineLineColor, ThicknessMeters);
        Lines.Add(bottomRight, bottomLeft, OutlineLineColor, ThicknessMeters);
        Lines.Add(bottomLeft, topLeft, OutlineLineColor, ThicknessMeters);
    }

    internal static float CalculateExpansionScale(float renderedLargestDimension)
    {
        var dimension = MathF.Max(0.001f, renderedLargestDimension);
        return Math.Clamp(1 + ((ThicknessMeters * 2) / dimension), 1.002f, 1.08f);
    }

    internal static Matrix ExpandAround(Matrix transform, Vec3 center, float scale) =>
        Matrix.T(-center)
        * Matrix.S(scale)
        * Matrix.T(center)
        * transform;

    private static Material CreateMaterial()
    {
        var material = Material.Unlit.Copy();
        material.FaceCull = Cull.Front;
        material.Transparency = Transparency.None;
        material.DepthTest = DepthTest.LessOrEq;
        material.DepthWrite = false;
        material.QueueOffset = 20;
        return material;
    }
}
