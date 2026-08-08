using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace StereoKitEditor.Assets;

internal static class PngThumbnailWriter
{
    private const int Width = 160;
    private const int Height = 120;

    public static void Write(string path, AssetBounds? bounds, bool hasError)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Thumbnail path has no parent directory."));
        if (File.Exists(path))
        {
            return;
        }

        var pixels = new byte[Width * Height * 4];
        PaintBackground(pixels);
        if (hasError)
        {
            DrawError(pixels);
        }
        else
        {
            DrawBounds(pixels, bounds);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var file = File.Create(temporaryPath);
            file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header, Width);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], Height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(file, "IHDR", header);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                for (var y = 0; y < Height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(pixels, y * Width * 4, Width * 4);
                }
            }

            WriteChunk(file, "IDAT", compressed.ToArray());
            WriteChunk(file, "IEND", []);
            file.Flush(flushToDisk: true);
            file.Close();
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && File.Exists(path))
            {
                // Another identical content-addressed import completed first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PaintBackground(byte[] pixels)
    {
        for (var y = 0; y < Height; y++)
        {
            var blend = y / (double)(Height - 1);
            var red = (byte)Math.Round(25 + (10 * blend));
            var green = (byte)Math.Round(29 + (12 * blend));
            var blue = (byte)Math.Round(35 + (17 * blend));
            for (var x = 0; x < Width; x++)
            {
                SetPixel(pixels, x, y, red, green, blue, 255);
            }
        }

        DrawLine(pixels, 18, Height - 18, Width - 18, Height - 18, 50, 57, 67, 255, 1);
    }

    private static void DrawBounds(byte[] pixels, AssetBounds? bounds)
    {
        var sizeX = Math.Max(bounds?.SizeX ?? 1, 0.0001);
        var sizeY = Math.Max(bounds?.SizeY ?? 1, 0.0001);
        var sizeZ = Math.Max(bounds?.SizeZ ?? 1, 0.0001);
        var largest = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
        var halfWidth = (int)Math.Clamp(42 * sizeX / largest, 13, 48);
        var halfHeight = (int)Math.Clamp(38 * sizeY / largest, 13, 42);
        var depthX = (int)Math.Clamp(20 * sizeZ / largest, 8, 22);
        var depthY = (int)Math.Clamp(13 * sizeZ / largest, 5, 15);
        const int centerX = Width / 2;
        const int centerY = Height / 2 + 4;

        var front = new[]
        {
            (centerX - halfWidth, centerY - halfHeight),
            (centerX + halfWidth, centerY - halfHeight),
            (centerX + halfWidth, centerY + halfHeight),
            (centerX - halfWidth, centerY + halfHeight),
        };
        var back = front.Select(point => (point.Item1 + depthX, point.Item2 - depthY)).ToArray();
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            DrawLine(pixels, back[index], back[next], 70, 156, 180, 255, 2);
            DrawLine(pixels, front[index], front[next], 89, 211, 192, 255, 2);
            DrawLine(pixels, front[index], back[index], 79, 181, 190, 255, 2);
        }

        DrawLine(pixels, centerX - 18, centerY, centerX + 18, centerY, 236, 192, 85, 255, 1);
        DrawLine(pixels, centerX, centerY - 18, centerX, centerY + 18, 236, 192, 85, 255, 1);
    }

    private static void DrawError(byte[] pixels)
    {
        DrawLine(pixels, 53, 33, 107, 87, 235, 78, 89, 255, 6);
        DrawLine(pixels, 107, 33, 53, 87, 235, 78, 89, 255, 6);
    }

    private static void DrawLine(
        byte[] pixels,
        (int X, int Y) start,
        (int X, int Y) end,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        int thickness) =>
        DrawLine(pixels, start.X, start.Y, end.X, end.Y, red, green, blue, alpha, thickness);

    private static void DrawLine(
        byte[] pixels,
        int x0,
        int y0,
        int x1,
        int y1,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        int thickness)
    {
        var deltaX = Math.Abs(x1 - x0);
        var stepX = x0 < x1 ? 1 : -1;
        var deltaY = -Math.Abs(y1 - y0);
        var stepY = y0 < y1 ? 1 : -1;
        var error = deltaX + deltaY;
        while (true)
        {
            for (var offsetY = -thickness / 2; offsetY <= thickness / 2; offsetY++)
            for (var offsetX = -thickness / 2; offsetX <= thickness / 2; offsetX++)
            {
                SetPixel(pixels, x0 + offsetX, y0 + offsetY, red, green, blue, alpha);
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var twiceError = 2 * error;
            if (twiceError >= deltaY)
            {
                error += deltaY;
                x0 += stepX;
            }

            if (twiceError <= deltaX)
            {
                error += deltaX;
                y0 += stepY;
            }
        }
    }

    private static void SetPixel(
        byte[] pixels,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            return;
        }

        var index = ((y * Width) + x) * 4;
        pixels[index] = red;
        pixels[index + 1] = green;
        pixels[index + 2] = blue;
        pixels[index + 3] = alpha;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
