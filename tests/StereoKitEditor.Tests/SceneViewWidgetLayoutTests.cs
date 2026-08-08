using StereoKitEditor.Protocol;
using StereoKitEditor.Runtime;

namespace StereoKitEditor.Tests;

public sealed class SceneViewWidgetLayoutTests
{
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(900, 1600)]
    [InlineData(640, 360)]
    public void Perspective_layout_stays_in_the_top_right_after_resize(int width, int height)
    {
        var layout = SceneViewWidgetLayout.Calculate(
            width,
            height,
            SceneProjection.Perspective,
            verticalFieldOfViewDegrees: 90,
            orthographicHeight: 1,
            depth: 0.38f);
        var verticalSpan = layout.WorldPerPixel * height;
        var halfWidth = verticalSpan * width / height * 0.5f;
        var halfHeight = verticalSpan * 0.5f;

        var rightInsetPixels = (halfWidth - layout.HorizontalWorldOffset) / layout.WorldPerPixel;
        var topInsetPixels = (halfHeight - layout.VerticalWorldOffset) / layout.WorldPerPixel;

        Assert.Equal(layout.EdgeInsetPixels, rightInsetPixels, precision: 3);
        Assert.Equal(layout.EdgeInsetPixels, topInsetPixels, precision: 3);
        Assert.True(layout.EdgeInsetPixels > layout.MarginPixels + layout.RadiusPixels);
    }

    [Fact]
    public void Orthographic_layout_uses_the_live_projection_height()
    {
        var layout = SceneViewWidgetLayout.Calculate(
            1200,
            600,
            SceneProjection.Orthographic,
            verticalFieldOfViewDegrees: 90,
            orthographicHeight: 2.4f,
            depth: 0.38f);

        Assert.Equal(2.4f / 600, layout.WorldPerPixel, precision: 6);
        Assert.True(layout.HorizontalWorldOffset > layout.VerticalWorldOffset);
    }

    [Fact]
    public void Tiny_viewports_keep_the_widget_center_inside_the_view()
    {
        var layout = SceneViewWidgetLayout.Calculate(
            40,
            30,
            SceneProjection.Perspective,
            verticalFieldOfViewDegrees: 90,
            orthographicHeight: 1,
            depth: 0.38f);

        Assert.True(layout.HorizontalWorldOffset >= 0);
        Assert.True(layout.VerticalWorldOffset >= 0);
        Assert.True(float.IsFinite(layout.WorldPerPixel));
        Assert.True(layout.MarginPixels + layout.RadiusPixels <= 15.001f);
    }
}
