# SKinny Editor icon assets

The application icon uses a compact `SK` monogram and a three-axis transform motif. It is designed to remain recognizable in Explorer and the Windows taskbar at 16–32 pixels.

## Files

- `SKinny.Editor.icon-source.png` — original generated chroma-key source.
- `SKinny.Editor.icon-master.png` — transparent, uncropped master.
- `SKinny.Editor.icon.png` — final transparent 512×512 application artwork.
- `SKinny.Editor.ico` — Windows multi-resolution icon containing 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel images.
- `favicon.ico` and `favicon-32.png` — browser/site variants.
- `apple-touch-icon.png` — 180×180 web-app/touch variant.
- `SKinny.Editor.icon-size-preview.png` — checkerboard size-legibility review sheet.

The executable consumes `src/StereoKitEditor.App/Assets/SKinny.Editor.ico`. Avalonia windows consume the same embedded resource, so the executable, title bar, taskbar, Alt+Tab, and dialogs share one identity.

## Generation prompt

Built-in image generation was used with this final brief:

> Create an original compact application icon for SKinny Editor, a visual editor for StereoKit. Use an exact uppercase `SK` geometric monogram, subtly suggesting a 3D editor viewport or transform axes. Center it on a dark charcoal rounded-square tile. Use thick cyan-teal and off-white strokes with one small warm amber axis-point accent. Keep the silhouette professional and recognizable at 16 pixels. No extra words, thin lines, watermark, mockup, or decorative clutter.

The source used a flat magenta chroma background. The installed image-generation helper removed it with a soft matte and despill before producing the final PNG and ICO sizes.
