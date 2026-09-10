using System;

namespace Godot
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ScriptPathAttribute : Attribute
    {
        public ScriptPathAttribute(string path)
        {
            Path = path;
        }

        public string Path { get; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GlobalClassAttribute : Attribute
    {
    }
}

namespace Spirectl.TestSymbols.Scenes
{
    [Godot.ScriptPath("res://scripts/CombatScreen.cs")]
    public sealed class CombatScreenController
    {
        public void _Ready()
        {
        }

        public void OnDeckChanged(Spirectl.TestSymbols.Gameplay.DeckController deck)
        {
            deck.DrawCard();
        }
    }

    [Godot.ScriptPath("res://scripts/HandPanelController.cs")]
    public sealed class HandPanelController
    {
        public void _GuiInput(string eventName)
        {
        }
    }

    [Godot.ScriptPath("res://scripts/StatusConfig.cs"), Godot.GlobalClass]
    public sealed class StatusConfig
    {
    }

    [Godot.ScriptPath("res://scripts/StatusBadge.cs"), Godot.GlobalClass]
    public sealed class StatusBadge
    {
    }
}
