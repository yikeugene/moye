using System.Windows.Media;

namespace Moye.Controls;

/// <summary>HSV conversion operates on sRGB channels; alpha is supplied separately.</summary>
public readonly record struct HsvColor(double Hue, double Saturation, double Value)
{
    public static HsvColor FromColor(Color color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var delta = maximum - minimum;
        var hue = delta == 0 ? 0 : maximum == r ? 60 * ((g - b) / delta % 6)
            : maximum == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return new(NormalizeHue(hue), maximum == 0 ? 0 : delta / maximum, maximum);
    }

    public Color ToColor(byte alpha = 255)
    {
        var hue = NormalizeHue(Hue);
        var saturation = Unit(Saturation); var value = Unit(Value);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var offset = value - chroma;
        var channels = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
        return Color.FromArgb(alpha, Channel(channels.Item1 + offset), Channel(channels.Item2 + offset), Channel(channels.Item3 + offset));
    }

    public static double NormalizeHue(double hue) => double.IsFinite(hue) ? (hue % 360 + 360) % 360 : 0;
    public static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
