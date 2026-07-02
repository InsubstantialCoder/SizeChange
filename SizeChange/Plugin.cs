using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SizeChange.Windows;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Vector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace SizeChange;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    //[PluginService] internal static IPluginLog Logger { get; private set; } = null!;

    private const string CommandName = "/sizechange";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SizeChange");
    private ConfigWindow ConfigWindow { get; init; }
    public Plugin()
    {
        Framework.Update += OnFrameworkUpdate;
        
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        
        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "opens the SizeChange config window"
        });
        
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        ConfigWindow.Toggle();
    }
    
    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        // disabled during pvp
        if (ClientState.IsPvP){ return; }
        
        // runs the first statement if not in party or shrink party is set to false, else iterates through entire party and shrinks members individually
        if (PartyList.Length == 0 || !Configuration.AlterParty)
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null) return;

            AdjustScale((Character*)player.Address);
        }
        else
        {
            foreach (var member in PartyList)
            {
                var actor = member.GameObject;
                if (actor == null) continue;
                
                AdjustScale((Character*)actor.Address);
            }
        }
    }

    // find the actor's health and shield value and uses that to adjust the model's scale
    public unsafe void AdjustScale(Character* actor)
    {
        if (actor == null) return;
        float maxhp = actor->MaxHealth;
        float shield = (actor->ShieldValue / 100f) * maxhp;
        float health = actor->Health + shield;
        float hpRatio = health / maxhp;
        //Logger.Information("hpRatio is {hpRatio}", hpRatio);
        float targetScale = Math.Clamp(hpRatio, Configuration.MinScale, 3.0f);

        var draw = (CharacterBase*)actor->DrawObject;

        if (draw != null)
        {
            float scale = draw->Scale.Y;
            scale = float.Lerp(scale, targetScale, Configuration.Speed / 60f);
            draw->Scale = new Vector3(scale, scale, scale);
        }
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
