namespace Beutl.Extensions.MotionTracking.Tracking;

/// <summary>
/// A single-channel image with values from 0 to 255. Pixel centres are at integer coordinates, so that a pixel of
/// a coarser pyramid level at x is centred on the finer level's pixel at 2x.
/// </summary>
internal sealed class GrayImage(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public float[] Pixels { get; } = new float[width * height];

    /// <summary>Fills the image with the Rec. 709 luma of 8-bit BGRA pixels.</summary>
    public void LoadBgra(ReadOnlySpan<byte> bgra, int rowBytes)
    {
        float[] pixels = Pixels;
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> row = bgra.Slice(y * rowBytes, Width * 4);
            int offset = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int i = x * 4;
                pixels[offset + x] = 0.0722f * row[i] + 0.7152f * row[i + 1] + 0.2126f * row[i + 2];
            }
        }
    }

    /// <summary>The value at a position between pixels. Positions outside the image take the nearest edge.</summary>
    public float Sample(float x, float y)
    {
        float fx = Math.Clamp(x, 0, Width - 1);
        float fy = Math.Clamp(y, 0, Height - 1);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        float ax = fx - x0;
        float ay = fy - y0;
        float[] pixels = Pixels;
        int row0 = y0 * Width;
        int row1 = y1 * Width;
        float top = pixels[row0 + x0] + (pixels[row0 + x1] - pixels[row0 + x0]) * ax;
        float bottom = pixels[row1 + x0] + (pixels[row1 + x1] - pixels[row1 + x0]) * ax;
        return top + (bottom - top) * ay;
    }

    /// <summary>Writes this image, filtered with a 5-tap Gaussian and halved in each direction, to the next level.</summary>
    public void DownsampleInto(GrayImage destination, float[] scratch)
    {
        int width2 = destination.Width;
        int height2 = destination.Height;
        float[] source = Pixels;

        // Horizontal pass: one filtered row of width2 samples for every source row.
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            int target = y * width2;
            for (int x2 = 0; x2 < width2; x2++)
            {
                int x = x2 * 2;
                int a = Math.Max(x - 2, 0);
                int b = Math.Max(x - 1, 0);
                int d = Math.Min(x + 1, Width - 1);
                int e = Math.Min(x + 2, Width - 1);
                scratch[target + x2] = (source[row + a] + source[row + e]
                                        + 4 * (source[row + b] + source[row + d])
                                        + 6 * source[row + x]) / 16;
            }
        }

        float[] output = destination.Pixels;
        for (int y2 = 0; y2 < height2; y2++)
        {
            int y = y2 * 2;
            int a = Math.Max(y - 2, 0) * width2;
            int b = Math.Max(y - 1, 0) * width2;
            int c = y * width2;
            int d = Math.Min(y + 1, Height - 1) * width2;
            int e = Math.Min(y + 2, Height - 1) * width2;
            int target = y2 * width2;
            for (int x2 = 0; x2 < width2; x2++)
            {
                output[target + x2] = (scratch[a + x2] + scratch[e + x2]
                                       + 4 * (scratch[b + x2] + scratch[d + x2])
                                       + 6 * scratch[c + x2]) / 16;
            }
        }
    }
}

/// <summary>
/// A frame at several resolutions. Each level is half the size of the one before it, and level L's coordinates are
/// level 0's divided by 2^L. The buffers are reused from frame to frame.
/// </summary>
internal sealed class ImagePyramid
{
    private const int MinLevelSize = 16;
    private const int MaxLevels = 6;

    private readonly GrayImage[] _levels;
    private readonly float[] _scratch;

    public ImagePyramid(int width, int height)
    {
        var levels = new List<GrayImage> { new(width, height) };
        while (levels.Count < MaxLevels && Math.Min(width, height) / 2 >= MinLevelSize)
        {
            width = (width + 1) / 2;
            height = (height + 1) / 2;
            levels.Add(new GrayImage(width, height));
        }

        _levels = [.. levels];
        _scratch = new float[levels.Count > 1 ? levels[1].Width * levels[0].Height : 0];
    }

    public int LevelCount => _levels.Length;

    public int Width => _levels[0].Width;

    public int Height => _levels[0].Height;

    public GrayImage this[int level] => _levels[level];

    /// <summary>Builds the coarser levels from level 0 after it was changed.</summary>
    public void BuildLevels()
    {
        for (int i = 1; i < _levels.Length; i++)
            _levels[i - 1].DownsampleInto(_levels[i], _scratch);
    }

    public void LoadBgra(ReadOnlySpan<byte> bgra, int rowBytes)
    {
        _levels[0].LoadBgra(bgra, rowBytes);
        BuildLevels();
    }
}
