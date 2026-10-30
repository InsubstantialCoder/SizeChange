using Dalamud.Configuration;
using System;

namespace SizeChange;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;
    
    // if true will shrink or grow any player
    public bool AlterAnyone { get; set; } = false;
    // the speed at which the model scales, higher is faster
    public float Speed { get; set; } = 2.0f;
    // the minimum size of the model
    public float MinScaleMultiplier { get; set; } = 0.1f;
    public float MaxScaleMultiplier { get; set; } = 1.0f;
    public bool OnlyActiveInCombat { get; set; } = false;
    public bool Enable { get; set; } = true;
    public bool GrowFromDamage { get; set; } = false;
    
    public void Save()
    {
        if (MinScaleMultiplier > MaxScaleMultiplier){ MinScaleMultiplier = MaxScaleMultiplier; }
        if (MaxScaleMultiplier < MinScaleMultiplier) { MaxScaleMultiplier = MinScaleMultiplier; }
        if (Speed <= 0)
        {
            Speed = 0.1f;
        }
        
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
