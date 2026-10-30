using System;
using System.Collections.Generic;
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

struct SCCharacterState {
    public float PlayerScale;
    public float PreviousScale;
}

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Logger { get; private set; } = null!;

    private const string CommandName_SizeChange = "/sizechange";
    private const string CommandName_Scale = "/scale";
    private const string Parameter_Enable = "enable";
    private const string Parameter_Disable = "disable";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SizeChange");
    private ConfigWindow ConfigWindow { get; init; }
    private Dictionary<uint, SCCharacterState> CharacterIdToLastScaleMap = new Dictionary<uint, SCCharacterState>();
    public Plugin()
    {
        Framework.Update += OnFrameworkUpdate;
        
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        
        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName_SizeChange, new CommandInfo(OnCommand)
        {
            HelpMessage = "opens the SizeChange config window"
        });

        CommandManager.AddHandler(CommandName_Scale, new CommandInfo(OnCommand)
        {
            HelpMessage = "sets character's scale"
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

        CommandManager.RemoveHandler(CommandName_SizeChange);
    }

    private unsafe void OnCommand(string command, string args)
    {
        if(command == CommandName_SizeChange)
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

        if(command == CommandName_Scale)
        {
            float from_arg;
            if(float.TryParse(args, out from_arg))
            {
                var player = ObjectTable.LocalPlayer;
                if (player != null) 
                {
                    UpdateScale((Character*)player.Address, from_arg);
                }
            }
        }
    }

    private unsafe void UpdateScale(Character* actor, float scale)
    {
        SCCharacterState charState;
        if(CharacterIdToLastScaleMap.ContainsKey(actor->EntityId))
        {
            charState = CharacterIdToLastScaleMap[actor->EntityId];
        }
        else
        {
            charState = new SCCharacterState();
        }
        charState.PlayerScale = scale;
        var draw = (CharacterBase*)actor->DrawObject;
        var currentScale = draw->Scale.Y;
        charState.PreviousScale = currentScale;

        CharacterIdToLastScaleMap[actor->EntityId] = charState;
    }
    
    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        bool disable = (ClientState.IsPvP || !Configuration.Enable|| (Configuration.OnlyActiveInCombat && !Condition[ConditionFlag.InCombat]));
        
        var player = ObjectTable.LocalPlayer;
            if (player == null) return;

        foreach (var thing in ObjectTable.PlayerObjects)
        {
            var actor = thing;
            if (actor == null) continue;
            bool isLocalPlayer = ((Character*)player.Address)->EntityId == ((Character*)thing.Address)->EntityId;
            
            AdjustScale((Character*)actor.Address, Configuration.GrowFromDamage, disable || (!isLocalPlayer && !Configuration.AlterAnyone));
        }
    }

    // find the actor's health and shield value and uses that to adjust the model's scale
    public unsafe void AdjustScale(Character* actor, bool growFromDamage, bool disable)
    {
        if (actor == null) return;
        float maxhp = actor->MaxHealth;
        float shield = (actor->ShieldValue / 100f) * maxhp;
        float health = actor->Health + shield;
        float hpRatio = health / maxhp;
        Logger.Information("hpRatio is {hpRatio}", hpRatio);

        var draw = (CharacterBase*)actor->DrawObject;

        if (draw != null)
        {
            float scale = draw->Scale.Y;
            Logger.Information("current scale is {scale}", scale);
            float previousScale = scale;
            SCCharacterState charState;
            if(CharacterIdToLastScaleMap.ContainsKey(actor->EntityId))
            {
                charState = CharacterIdToLastScaleMap[actor->EntityId];
                previousScale = charState.PreviousScale;
            }
            else
            {
                charState = new SCCharacterState();
                charState.PlayerScale = 1.0f;
            }
            Logger.Information("Previous scale is {scale}", previousScale);
            if (previousScale != scale)
            {
                charState.PlayerScale = scale;
            }
            float targetScale = disable ? charState.PlayerScale : growFromDamage ? 
            Math.Clamp(Configuration.MaxScaleMultiplier - (Configuration.MaxScaleMultiplier * hpRatio), Configuration.MinScaleMultiplier, Configuration.MaxScaleMultiplier)*charState.PlayerScale : 
            Math.Clamp(hpRatio, Configuration.MinScaleMultiplier, float.PositiveInfinity)*charState.PlayerScale;
            Logger.Information("targetScale is {targetScale}", targetScale);

            
            scale = float.Lerp(previousScale, targetScale, Configuration.Speed / 100f);
            Logger.Information("scale after lerp is {scale}", scale);
            draw->Scale = new Vector3(scale, scale, scale);
            actor->Scale = scale;
            charState.PreviousScale = scale;
            CharacterIdToLastScaleMap[actor->EntityId] = charState;
        }
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
