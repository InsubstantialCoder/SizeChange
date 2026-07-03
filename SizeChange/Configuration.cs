using Dalamud.Configuration;
using System;

namespace SizeChange;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;
    
    // if true will shrink all members in the party, false will only shrink the player
    public bool AlterParty { get; set; } = true;
    // the speed at which the model scales, higher is faster
    public float Speed { get; set; } = 2.0f;
    // the minimum size of the model
    public float MinScale { get; set; } = 0.1f;
    public float MaxScale { get; set; } = 1.0f;
    public bool OnlyActiveInCombat { get; set; } = false;
    public bool Enable { get; set; } = true;
    public bool GrowFromDamage { get; set; } = false;
    
    public void Save()
    {
        if (MinScale < 0.01f){ MinScale = 0.01f; }
        if (MaxScale < 1.0f) { MaxScale = 1.0f; }
        
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
