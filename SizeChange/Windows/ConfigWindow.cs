using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SizeChange.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    
    public ConfigWindow(Plugin plugin) : base("SizeChange Config")
    {

        Size = new Vector2(350, 280);
        SizeCondition = ImGuiCond.Always;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var speed = configuration.Speed;
        var minScale = configuration.MinScale;
        var maxScale = configuration.MaxScale;
        var AlterParty = configuration.AlterParty;
        var Enable = configuration.Enable;
        var OnlyActiveInCombat = configuration.OnlyActiveInCombat;

        if (ImGui.DragFloat("Minimum Size", ref minScale, 0.01F, 0.01F, 1.00F))
        {
            if (minScale > 1.00F){ minScale = 1.00F; }
            configuration.MinScale = minScale;
            configuration.Save();
        }

        if (ImGui.DragFloat("Maximum Size", ref maxScale, 0.1F, 0.01F, 10.00F))
        {
            if (maxScale < 1.00F){ maxScale = 1.00F; }
            configuration.MaxScale = maxScale;
            configuration.Save();
        }

        if (ImGui.DragFloat("Speed", ref speed, 0.1F, 0.1F, 100.0F))
        {
            configuration.Speed = speed;
            configuration.Save();
        }
        
        if (ImGui.Checkbox("Scale Party", ref AlterParty))
        {
            configuration.AlterParty = AlterParty;
            configuration.Save();
        }

        if (ImGui.Checkbox("Enable", ref Enable))
        {
            configuration.Enable = Enable;
            configuration.Save();
        }

        if (ImGui.Checkbox("Only Active in Combat", ref OnlyActiveInCombat))
        {
            configuration.OnlyActiveInCombat = OnlyActiveInCombat;
            configuration.Save();
        }
        

        if (ImGui.Button("Default"))
        {
            configuration.AlterParty = true;
            configuration.MinScale = 0.1f;
            configuration.MaxScale = 1.0f;
            configuration.Speed = 2.0f;
            configuration.Save();
        }
        
        ImGui.Text("This plugin is disabled in PVP");
    }
}
