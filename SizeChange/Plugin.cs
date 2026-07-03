using System;
using System.Diagnostics;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SizeChange.Windows;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Vector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace SizeChange;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Logger { get; private set; } = null!;

    private const string CommandName = "/sizechange";
    private const string Parameter_Enable = "enable";
    private const string Parameter_Disable = "disable";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SizeChange");
    private ConfigWindow ConfigWindow { get; init; }
    //private Dictionary<uint, long> _characterIdToTimestampMap = new Dictionary<uint, long>();
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
        if(args == "")
        {
            ConfigWindow.Toggle();
        }

        if(args == Parameter_Enable)
        {
            Configuration.Enable = true;
        }
        if(args == Parameter_Disable)
        {
            Configuration.Enable = false;
        }
    }
    
    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        // disabled during pvp
        if (ClientState.IsPvP || !Configuration.Enable|| (Configuration.OnlyActiveInCombat && !Condition[ConditionFlag.InCombat]))
        { 
            Logger.Information("Update is disabled");
            return; 
        }
        
        // runs the first statement if not in party or shrink party is set to false, else iterates through entire party and shrinks members individually
        if (PartyList.Length == 0 || !Configuration.AlterParty)
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null) return;

            if(Configuration.GrowFromDamage)
            {
                AdjustScaleGrowFromDamage((Character*)player.Address);
            }
            else 
            {
                AdjustScale((Character*)player.Address);
            }
        }
        else
        {
            foreach (var member in PartyList)
            {
                var actor = member.GameObject;
                if (actor == null) continue;
                
                if(Configuration.GrowFromDamage)
                {
                    AdjustScaleGrowFromDamage((Character*)actor.Address);
                }
                else
                {
                    AdjustScale((Character*)actor.Address);
                }
            }
        }
    }

    public unsafe void AdjustScaleGrowFromDamage(Character* actor)
    {
        if (actor == null) return;
        float maxhp = actor->MaxHealth;
        float shield = (actor->ShieldValue / 100f) * maxhp;
        float health = actor->Health + shield;
        float hpRatio = health / maxhp;
        Logger.Information("hpRatio is {hpRatio}", hpRatio);
        float targetScale = Math.Clamp(Configuration.MaxScale - (Configuration.MaxScale * hpRatio), 
            Configuration.MinScale, Configuration.MaxScale);
        Logger.Information("targetScale is {targetScale}", targetScale);

        var draw = (CharacterBase*)actor->DrawObject;

        if (draw != null)
        {
            float scale = draw->Scale.Y;
            Logger.Information("current scale is {scale}", scale);
            
            scale = float.Lerp(scale, targetScale, Configuration.Speed / 100f);
            Logger.Information("scale after lerp is {scale}", scale);
            draw->Scale = new Vector3(scale, scale, scale);
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
        Logger.Information("hpRatio is {hpRatio}", hpRatio);
        float targetScale = Math.Clamp(hpRatio, Configuration.MinScale, Configuration.MaxScale);
        Logger.Information("targetScale is {targetScale}", targetScale);

        var draw = (CharacterBase*)actor->DrawObject;

        if (draw != null)
        {
            float scale = draw->Scale.Y;
            Logger.Information("current scale is {scale}", scale);
            
            scale = float.Lerp(scale, targetScale, Configuration.Speed / 100f);
            Logger.Information("scale after lerp is {scale}", scale);
            draw->Scale = new Vector3(scale, scale, scale);
        }
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
