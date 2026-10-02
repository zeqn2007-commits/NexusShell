using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nexus.App.Shell;

/// <summary>
/// The transparent window materials of Настройки › Внешний вид: Mica tinted by the wallpaper, or acrylic glass
/// that lets the blurred desktop and windows show through. Unlike the stock backdrops they stay on while the
/// window is inactive, as in customised Windows builds, and <see cref="Transparency"/> sets how strong they are.
/// </summary>
public sealed partial class TransparentBackdrop(bool glass) : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, Target> _targets = [];
    private double _transparency = 0.5;

    public bool IsGlass { get; } = glass;

    /// <summary>0…1.</summary>
    public double Transparency
    {
        get => _transparency;
        set
        {
            _transparency = Math.Clamp(value, 0, 1);
            foreach (var target in _targets.Values)
            {
                Configure(target);
            }
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        var defaults = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        var target = new Target(
            IsGlass ? new DesktopAcrylicController() : null,
            IsGlass ? null : new MicaController(),
            new SystemBackdropConfiguration { IsInputActive = true, Theme = defaults.Theme, IsHighContrast = defaults.IsHighContrast });
        Configure(target);
        if (target.Glass is { } acrylic)
        {
            acrylic.AddSystemBackdropTarget(connectedTarget);
            acrylic.SetSystemBackdropConfiguration(target.Configuration);
        }
        else if (target.Mica is { } mica)
        {
            mica.AddSystemBackdropTarget(connectedTarget);
            mica.SetSystemBackdropConfiguration(target.Configuration);
        }

        _targets[connectedTarget] = target;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_targets.Remove(disconnectedTarget, out var target))
        {
            target.Glass?.RemoveSystemBackdropTarget(disconnectedTarget);
            target.Glass?.Dispose();
            target.Mica?.RemoveSystemBackdropTarget(disconnectedTarget);
            target.Mica?.Dispose();
        }
    }

    /// <summary>Follows the light/dark theme; activation is ignored on purpose (the material never falls back to a flat colour).</summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (_targets.TryGetValue(target, out var entry))
        {
            var defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
            entry.Configuration.Theme = defaults.Theme;
            entry.Configuration.IsHighContrast = defaults.IsHighContrast;
            Configure(entry);
        }
    }

    private void Configure(Target target)
    {
        var light = target.Configuration.Theme == SystemBackdropTheme.Light;
        var tint = light ? Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
        var fallback = light ? Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9) : Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
        var clear = (float)_transparency;
        if (target.Glass is { } acrylic)
        {
            acrylic.Kind = DesktopAcrylicKind.Thin;
            acrylic.TintColor = tint;
            acrylic.FallbackColor = fallback;
            acrylic.TintOpacity = 0.85f * (1 - clear);
            acrylic.LuminosityOpacity = 0.95f - 0.8f * clear;
        }
        else if (target.Mica is { } mica)
        {
            mica.Kind = MicaKind.BaseAlt;
            mica.TintColor = tint;
            mica.FallbackColor = fallback;
            mica.TintOpacity = 0.8f * (1 - clear);
            mica.LuminosityOpacity = 1f - 0.5f * clear;
        }
    }

    private sealed record Target(DesktopAcrylicController? Glass, MicaController? Mica, SystemBackdropConfiguration Configuration);
}
