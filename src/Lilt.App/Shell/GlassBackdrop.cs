using Lilt.Core.Theming;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Lilt.App.Shell;

// Mica or Acrylic with the theme's tint settings. Stays "active" when the window loses focus,
// so the glass doesn't turn solid every time you click another app.
sealed class GlassBackdrop(BackdropSpec spec, ThemeBase themeBase, Rgba fallback) : SystemBackdrop
{
    ISystemBackdropControllerWithTargets? controller;

    public static bool IsSupported(Backdrop material) => material switch
    {
        Backdrop.Mica or Backdrop.MicaAlt => MicaController.IsSupported(),
        Backdrop.Acrylic or Backdrop.AcrylicThin => DesktopAcrylicController.IsSupported(),
        _ => false,
    };

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        var config = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = themeBase == ThemeBase.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark,
        };
        var fallbackColor = ThemeResources.ToColor(fallback.Opaque);
        if (spec.Material is Backdrop.Mica or Backdrop.MicaAlt)
        {
            var mica = new MicaController { Kind = spec.Material == Backdrop.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base, FallbackColor = fallbackColor };
            if (spec.Tint is { } tint) mica.TintColor = ThemeResources.ToColor(tint.Opaque);
            if (spec.TintOpacity is { } t) mica.TintOpacity = (float)t;
            if (spec.LuminosityOpacity is { } l) mica.LuminosityOpacity = (float)l;
            controller = mica;
        }
        else
        {
            var acrylic = new DesktopAcrylicController { Kind = spec.Material == Backdrop.AcrylicThin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base, FallbackColor = fallbackColor };
            if (spec.Tint is { } tint) acrylic.TintColor = ThemeResources.ToColor(tint.Opaque);
            if (spec.TintOpacity is { } t) acrylic.TintOpacity = (float)t;
            if (spec.LuminosityOpacity is { } l) acrylic.LuminosityOpacity = (float)l;
            controller = acrylic;
        }
        controller.AddSystemBackdropTarget(connectedTarget);
        controller.SetSystemBackdropConfiguration(config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        (controller as IDisposable)?.Dispose();
        controller = null;
    }
}
