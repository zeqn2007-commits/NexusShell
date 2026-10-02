using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Models;
using Windows.UI;

namespace Nexus.App.Controls;

/// <summary>
/// Fluent glyph coloured by file category. With <see cref="ShowTile"/> the glyph
/// sits on a soft tinted square (home tiles, details pane); without it the glyph
/// is drawn alone (file lists). Real shell icons replace it when available.
/// </summary>
public sealed partial class FileIcon : Grid
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(FileKind), typeof(FileIcon), new PropertyMetadata(FileKind.Other, OnVisualPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FileIcon), new PropertyMetadata(20d, OnVisualPropertyChanged));

    public static readonly DependencyProperty ShowTileProperty = DependencyProperty.Register(
        nameof(ShowTile), typeof(bool), typeof(FileIcon), new PropertyMetadata(false, OnVisualPropertyChanged));

    private readonly Border _tile = new();
    private readonly FontIcon _glyph = new();

    public FileIcon()
    {
        _glyph.FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
        _glyph.HorizontalAlignment = HorizontalAlignment.Center;
        _glyph.VerticalAlignment = VerticalAlignment.Center;
        _tile.Child = _glyph;
        Children.Add(_tile);
        ActualThemeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => UpdateVisuals();
        IsHitTestVisible = false;
    }

    public FileKind Kind
    {
        get => (FileKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool ShowTile
    {
        get => (bool)GetValue(ShowTileProperty);
        set => SetValue(ShowTileProperty, value);
    }

    public static string GlyphFor(FileKind kind) => kind switch
    {
        FileKind.Folder => "\uE8D5",
        FileKind.Document => "\uE8A5",
        FileKind.Spreadsheet => "\uE9F9",
        FileKind.Presentation => "\uE8A5",
        FileKind.Pdf => "\uE8A5",
        FileKind.Image => "\uEB9F",
        FileKind.Video => "\uE714",
        FileKind.Audio => "\uEC4F",
        FileKind.Archive => "\uF012",
        FileKind.Code => "\uE943",
        FileKind.Application => "\uECAA",
        FileKind.Text => "\uE7C3",
        FileKind.Torrent => "\uE896",
        FileKind.Drive => "\uEDA2",
        FileKind.Computer => "\uE977",
        FileKind.NetworkDevice => "\uE968",
        _ => "\uE7C3"
    };

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FileIcon)d).UpdateVisuals();

    private void UpdateVisuals()
    {
        var isLight = ActualTheme == ElementTheme.Light;
        var color = FileKindPalette.Get(Kind, isLight);
        _glyph.Glyph = GlyphFor(Kind);
        _glyph.Foreground = new SolidColorBrush(color);

        if (ShowTile)
        {
            var tileSize = IconSize;
            _glyph.FontSize = Math.Round(tileSize * 0.5);
            _tile.Width = tileSize;
            _tile.Height = tileSize;
            _tile.CornerRadius = new CornerRadius(Math.Max(4, tileSize * 0.22));
            _tile.Background = new SolidColorBrush(Color.FromArgb(isLight ? (byte)0x1F : (byte)0x2E, color.R, color.G, color.B));
        }
        else
        {
            _glyph.FontSize = IconSize;
            _tile.Width = double.NaN;
            _tile.Height = double.NaN;
            _tile.Background = new SolidColorBrush(Colors.Transparent);
        }
    }
}

internal static class FileKindPalette
{
    public static Color Get(FileKind kind, bool light) => kind switch
    {
        FileKind.Folder => light ? Rgb(0xD9, 0x9A, 0x1E) : Rgb(0xF6, 0xC3, 0x5B),
        FileKind.Document => light ? Rgb(0x2F, 0x6F, 0xE0) : Rgb(0x6E, 0xA8, 0xFF),
        FileKind.Spreadsheet => light ? Rgb(0x1F, 0x9D, 0x63) : Rgb(0x4F, 0xCB, 0x8D),
        FileKind.Presentation => light ? Rgb(0xD9, 0x5A, 0x2B) : Rgb(0xFF, 0x8A, 0x5B),
        FileKind.Pdf => light ? Rgb(0xD1, 0x3B, 0x3B) : Rgb(0xFF, 0x6B, 0x6B),
        FileKind.Image => light ? Rgb(0x8F, 0x4F, 0xE0) : Rgb(0xC4, 0x8B, 0xFF),
        FileKind.Video => light ? Rgb(0xD1, 0x3F, 0x78) : Rgb(0xFF, 0x6F, 0xA3),
        FileKind.Audio => light ? Rgb(0x15, 0x8F, 0xA0) : Rgb(0x3F, 0xD0, 0xE0),
        FileKind.Archive => light ? Rgb(0xA0, 0x6E, 0x2E) : Rgb(0xD9, 0xA8, 0x6C),
        FileKind.Code => light ? Rgb(0x4F, 0x6A, 0x92) : Rgb(0x9D, 0xB4, 0xD8),
        FileKind.Application => light ? Rgb(0x47, 0x5C, 0xD6) : Rgb(0x8C, 0x9B, 0xFA),
        FileKind.Torrent => light ? Rgb(0x2E, 0x8B, 0x57) : Rgb(0x6F, 0xD3, 0x9C),
        FileKind.Drive => light ? Rgb(0x5E, 0x66, 0x72) : Rgb(0xB8, 0xC0, 0xCC),
        FileKind.Computer => light ? Rgb(0x2F, 0x6F, 0xE0) : Rgb(0x6E, 0xA8, 0xFF),
        FileKind.NetworkDevice => light ? Rgb(0x15, 0x8F, 0xA0) : Rgb(0x3F, 0xD0, 0xE0),
        _ => light ? Rgb(0x5E, 0x66, 0x72) : Rgb(0xB8, 0xC0, 0xCC)
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
}
