using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Nexus.App.Converters;

/// <summary>Small pure functions used from x:Bind expressions.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static ImageSource? Image(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new BitmapImage(new Uri(path))
        {
            DecodePixelWidth = decodeWidth,
            DecodePixelType = DecodePixelType.Logical
        };
    }

    /// <summary>
    /// A fresh icon for a glyph. TabViewItem builds its icon once from IconSource and ignores later
    /// glyph changes, so a tab that navigates elsewhere needs a new IconSource object.
    /// </summary>
    public static Microsoft.UI.Xaml.Controls.IconSource GlyphIcon(string glyph) =>
        new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = glyph };

    /// <summary>A diagonal gradient, e.g. a game's generated artwork colours.</summary>
    public static Brush Gradient(string? from, string? to) => new LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = Color(from ?? "#FF3A4A6B"), Offset = 0 },
            new GradientStop { Color = Color(to ?? "#FF1E2638"), Offset = 1 }
        }
    };

    public static Color Color(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        if (hex.Length <= 7)
        {
            value |= 0xFF000000;
        }

        return Windows.UI.Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);
    }
}
