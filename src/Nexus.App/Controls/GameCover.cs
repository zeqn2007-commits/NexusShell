using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Converters;
using Nexus.App.Models;
using Windows.Foundation;

namespace Nexus.App.Controls;

/// <summary>
/// Portrait game artwork. Uses the launcher cover when it exists, otherwise draws
/// a calm gradient poster with the game's monogram so the grid never has holes.
/// </summary>
public sealed partial class GameCover : Grid
{
    public static readonly DependencyProperty GameProperty = DependencyProperty.Register(
        nameof(Game), typeof(GameItem), typeof(GameCover), new PropertyMetadata(null, OnGameChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth), typeof(int), typeof(GameCover), new PropertyMetadata(320));

    public GameCover()
    {
        CornerRadius = new CornerRadius(8);
        IsHitTestVisible = false;
    }

    public GameItem? Game
    {
        get => (GameItem?)GetValue(GameProperty);
        set => SetValue(GameProperty, value);
    }

    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    private static void OnGameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GameCover)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (Game is not { } game)
        {
            return;
        }

        if (game.HasCover)
        {
            Children.Add(new Image
            {
                Source = Ui.Image(game.CoverPath, DecodeWidth),
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Ui.Color(game.PaletteFrom), Offset = 0 },
                new GradientStop { Color = Ui.Color(game.PaletteTo), Offset = 1 }
            }
        };

        var viewbox = new Viewbox
        {
            Margin = new Thickness(18),
            MaxHeight = 72,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = game.Monogram,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.9 }
            }
        };
        Children.Add(viewbox);
        Children.Add(new TextBlock
        {
            Text = game.Name,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.85 }
        });
    }
}
