using System.Buffers.Binary;
using System.Text;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.Graphics;
using Beutl.Media;

namespace Beutl.Extensions.MotionTracking.Tests;

/// <summary>
/// Frames for testing the trackers without Beutl: a random texture seen through an exactly known motion, sampled so
/// that positions between pixels are as accurate as the bilinear interpolation allows.
/// </summary>
internal sealed class SyntheticVideo
{
    private readonly float[] _texture;
    private readonly int _textureSize;
    private readonly Random _noise;

    public SyntheticVideo(int width = 320, int height = 240, int seed = 1)
    {
        Width = width;
        Height = height;
        _noise = new Random(seed + 1);
        _textureSize = 1024;
        _texture = MakeTexture(_textureSize, seed);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Random blobs at several sizes, like the detail of a real scene. The pattern does not repeat.</summary>
    private static float[] MakeTexture(int size, int seed)
    {
        var random = new Random(seed);
        var result = new float[size * size];
        foreach ((int cell, float amplitude) in new[] { (32, 60f), (16, 45f), (8, 35f), (4, 25f) })
        {
            int count = size / cell + 2;
            var grid = new float[count * count];
            for (int i = 0; i < grid.Length; i++)
                grid[i] = (float)(random.NextDouble() * 2 - 1) * amplitude;

            for (int y = 0; y < size; y++)
            {
                float fy = (float)y / cell;
                int y0 = (int)fy;
                float ay = Smooth(fy - y0);
                for (int x = 0; x < size; x++)
                {
                    float fx = (float)x / cell;
                    int x0 = (int)fx;
                    float ax = Smooth(fx - x0);
                    float top = grid[y0 * count + x0] + (grid[y0 * count + x0 + 1] - grid[y0 * count + x0]) * ax;
                    float bottom = grid[(y0 + 1) * count + x0] + (grid[(y0 + 1) * count + x0 + 1] - grid[(y0 + 1) * count + x0]) * ax;
                    result[y * size + x] += top + (bottom - top) * ay;
                }
            }
        }

        for (int i = 0; i < result.Length; i++)
            result[i] = Math.Clamp(result[i] + 128, 0, 255);
        return result;

        static float Smooth(float t) => t * t * (3 - 2 * t);
    }

    /// <summary>Writes the texture to an image file, for scenes that go through Beutl's renderer.</summary>
    public void SaveTexture(string path)
    {
        using var bitmap = new Bitmap(_textureSize, _textureSize, BitmapColorType.Bgra8888, BitmapAlphaType.Premul);
        for (int y = 0; y < _textureSize; y++)
        {
            Span<byte> row = bitmap.GetRow(y);
            for (int x = 0; x < _textureSize; x++)
            {
                byte value = (byte)Math.Clamp(_texture[y * _textureSize + x], 0, 255);
                row[x * 4] = value;
                row[x * 4 + 1] = value;
                row[x * 4 + 2] = value;
                row[x * 4 + 3] = 255;
            }
        }

        bitmap.Save(path, EncodedImageFormat.Png);
    }

    // The texture moving by whole pixels, as an animated PNG of 30 frames a second, for scenes that hold it as a video.
    // A point at (x, y) in the first frame is at (x + speedX * frame, y + speedY * frame) later on.
    public void SaveMovingVideo(string path, int speedX, int speedY, int frames)
    {
        using FileStream file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        uint sequence = 0;
        for (int frame = 0; frame <= frames; frame++)
        {
            ImagePyramid image = Render(0, 0, new TrackPose(Width / 2 + speedX * frame, Height / 2 + speedY * frame));
            List<(string Type, byte[] Data)> chunks = EncodePng(image[0]);
            if (frame == 0)
            {
                WriteChunk(file, "IHDR", chunks.First(chunk => chunk.Type == "IHDR").Data);
                WriteChunk(file, "acTL", [.. BigEndian((uint)frames + 1), .. BigEndian(0)]);
            }

            // Each frame replaces the one before for 1/30 of a second.
            WriteChunk(file, "fcTL",
                [.. BigEndian(sequence++), .. BigEndian((uint)Width), .. BigEndian((uint)Height), .. BigEndian(0), .. BigEndian(0), 0, 1, 0, 30, 0, 0]);
            foreach (byte[] data in chunks.Where(chunk => chunk.Type == "IDAT").Select(chunk => chunk.Data))
            {
                if (frame == 0)
                    WriteChunk(file, "IDAT", data);
                else
                    WriteChunk(file, "fdAT", [.. BigEndian(sequence++), .. data]);
            }
        }

        WriteChunk(file, "IEND", []);
    }

    private static List<(string Type, byte[] Data)> EncodePng(GrayImage image)
    {
        using var bitmap = new Bitmap(image.Width, image.Height, BitmapColorType.Gray8, BitmapAlphaType.Opaque);
        for (int y = 0; y < image.Height; y++)
        {
            Span<byte> row = bitmap.GetRow(y);
            for (int x = 0; x < image.Width; x++)
                row[x] = (byte)Math.Clamp(image.Pixels[y * image.Width + x], 0, 255);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, EncodedImageFormat.Png);
        byte[] bytes = png.ToArray();
        var chunks = new List<(string, byte[])>();
        for (int offset = 8; offset < bytes.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            chunks.Add((Encoding.ASCII.GetString(bytes, offset + 4, 4), bytes[(offset + 8)..(offset + 8 + length)]));
            offset += length + 12;
        }

        return chunks;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(BigEndian((uint)data.Length));
        stream.Write(typed);
        stream.Write(BigEndian(Crc32(typed)));
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }

    private float SampleTexture(double x, double y)
    {
        // The texture wraps, so that long motions never run out of it.
        double fx = x - Math.Floor(x / _textureSize) * _textureSize;
        double fy = y - Math.Floor(y / _textureSize) * _textureSize;
        int x0 = (int)fx % _textureSize;
        int y0 = (int)fy % _textureSize;
        int x1 = (x0 + 1) % _textureSize;
        int y1 = (y0 + 1) % _textureSize;
        float ax = (float)(fx - Math.Floor(fx));
        float ay = (float)(fy - Math.Floor(fy));
        float top = _texture[y0 * _textureSize + x0] + (_texture[y0 * _textureSize + x1] - _texture[y0 * _textureSize + x0]) * ax;
        float bottom = _texture[y1 * _textureSize + x0] + (_texture[y1 * _textureSize + x1] - _texture[y1 * _textureSize + x0]) * ax;
        return top + (bottom - top) * ay;
    }

    /// <summary>
    /// Renders the texture moved so that what was at (centerX, centerY) of frame zero appears at the given pose.
    /// </summary>
    public ImagePyramid Render(
        double originX,
        double originY,
        TrackPose pose,
        float gain = 1,
        float bias = 0,
        float noise = 0,
        Func<int, int, float?>? overlay = null)
    {
        var pyramid = new ImagePyramid(Width, Height);
        GrayImage image = pyramid[0];
        double cos = Math.Cos(pose.Rotation) / pose.Scale;
        double sin = Math.Sin(pose.Rotation) / pose.Scale;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double dx = x - pose.X;
                double dy = y - pose.Y;
                double u = cos * dx + sin * dy;
                double v = -sin * dx + cos * dy;
                float value = SampleTexture(originX + u, originY + v) * gain + bias;
                if (noise > 0)
                    value += (float)(_noise.NextDouble() * 2 - 1) * noise;
                if (overlay?.Invoke(x, y) is { } covered)
                    value = covered;
                image.Pixels[y * Width + x] = Math.Clamp(value, 0, 255);
            }
        }

        pyramid.BuildLevels();
        return pyramid;
    }

    /// <summary>Runs a tracker through poses and returns what it found, stopping at the first frame it loses.</summary>
    public static List<TrackResult> Track(
        PatchTracker tracker,
        IReadOnlyList<ImagePyramid> frames,
        TrackPose start)
    {
        tracker.Start(frames[0], start);
        var results = new List<TrackResult>();
        for (int i = 1; i < frames.Count; i++)
        {
            TrackResult result = tracker.Step(frames[i]);
            results.Add(result);
            if (result.Status != TrackStatus.Tracked)
                break;
        }

        return results;
    }
}
