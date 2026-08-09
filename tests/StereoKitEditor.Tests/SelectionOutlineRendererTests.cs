using StereoKit;
using StereoKitEditor.Runtime;

namespace StereoKitEditor.Tests;

public sealed class SelectionOutlineRendererTests
{
    [Fact]
    public void Expansion_scale_targets_a_two_millimeter_outline()
    {
        var scale = SelectionOutlineRenderer.CalculateExpansionScale(0.20f);

        Assert.Equal(1.02f, scale, precision: 4);
    }

    [Fact]
    public void Expansion_scale_is_bounded_for_tiny_geometry()
    {
        var scale = SelectionOutlineRenderer.CalculateExpansionScale(0.00001f);

        Assert.Equal(1.08f, scale, precision: 4);
    }

    [Fact]
    public void Expansion_keeps_an_off_center_mesh_center_fixed()
    {
        var center = new Vec3(1, -2, 0.5f);
        var transform = Matrix.TRS(new Vec3(4, 3, -2), Quat.Identity, 2);
        var expanded = SelectionOutlineRenderer.ExpandAround(transform, center, 1.04f);

        var originalCenter = transform.Transform(center);
        var expandedCenter = expanded.Transform(center);
        var originalEdge = transform.Transform(center + Vec3.Right);
        var expandedEdge = expanded.Transform(center + Vec3.Right);

        Assert.Equal(originalCenter.x, expandedCenter.x, precision: 4);
        Assert.Equal(originalCenter.y, expandedCenter.y, precision: 4);
        Assert.Equal(originalCenter.z, expandedCenter.z, precision: 4);
        Assert.Equal(
            Vec3.Distance(originalCenter, originalEdge) * 1.04f,
            Vec3.Distance(expandedCenter, expandedEdge),
            precision: 4);
    }
}
