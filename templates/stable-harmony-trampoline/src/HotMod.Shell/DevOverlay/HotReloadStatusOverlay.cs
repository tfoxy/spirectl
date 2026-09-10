using HotMod.Shell.Diagnostics;
using HotMod.Shell.Runtime;

namespace HotMod.Shell.DevOverlay;

public static class HotReloadStatusOverlay
{
    public static void TryInstall(HotRuntime runtime)
    {
#if HOTMOD_STS2
        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            var root = tree?.Root;
            if (root is null)
            {
                ReloadLog.WriteWarning("hot reload overlay was not installed because the scene tree root was unavailable");
                return;
            }

            root.AddChild(new HotReloadStatusOverlayNode(runtime));
            ReloadLog.WriteInfo("hot reload overlay installed");
        }
        catch (Exception exception)
        {
            ReloadLog.WriteWarning("hot reload overlay failed to install", new
            {
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message
            });
        }
#else
        _ = runtime;
#endif
    }
}

#if HOTMOD_STS2
internal sealed partial class HotReloadStatusOverlayNode : Godot.CanvasLayer
{
    private readonly HotRuntime _runtime;
    private readonly Godot.Label _label = new();
    private double _elapsed;

    public HotReloadStatusOverlayNode(HotRuntime runtime)
    {
        _runtime = runtime;
        Layer = 100;
    }

    public override void _Ready()
    {
        _label.Position = new Godot.Vector2(12, 12);
        _label.AddThemeColorOverride("font_color", new Godot.Color(1f, 1f, 1f, 0.95f));
        _label.AddThemeColorOverride("font_shadow_color", new Godot.Color(0f, 0f, 0f, 0.8f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);
        AddChild(_label);
        UpdateText();
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < 0.25)
        {
            return;
        }

        _elapsed = 0;
        UpdateText();
    }

    private void UpdateText()
    {
        _label.Text = HotReloadStatusOverlayModel
            .FromStatus(_runtime.CreateSpirectlStatusSnapshot())
            .ToDisplayText();
    }
}
#endif
