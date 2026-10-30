using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Threading;
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

struct SCContentIDScale {
    public ulong ContentId;
    public float Scale;
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
    private const int MS_PER_FRAME = 33;
    private const int MAX_ADJ_PLAYERS = 99;
    private const int MAX_RESPONSE_COUNT = 5400;
    private const int SIZE_VERSION = sizeof(ushort);
    private const int SIZE_SCALE = sizeof(float);
    private const int SIZE_CONTENTID = sizeof(ulong);
    private const int SIZE_SEQ = sizeof(uint);
    private const int SIZE_HANDLE = sizeof(ushort);
    private const ushort VERSION = 1;

    public Configuration Configuration { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    public readonly WindowSystem WindowSystem = new("SizeChange");
    private ulong[] AdjacentPlayerContentIds = new ulong[MAX_ADJ_PLAYERS];
    private ConcurrentQueue<SCContentIDScale[]> CharacterUpdates = new ConcurrentQueue<SCContentIDScale[]>();
    private Dictionary<ulong, SCCharacterState> ContentIdToCharacterScaleMap = new Dictionary<ulong, SCCharacterState>();

    private UdpClient udpClient;
    private Thread udpSendThread;
    private Thread udpReceiveThread;
    private Stopwatch sendTimer;
    private ulong localContentId = 0;
    private volatile bool syncing = true;
    private volatile float localScale;
    private volatile ushort userHandle;

    private readonly Stopwatch lastVerifiedReply = Stopwatch.StartNew();
    private const int ReplyTimeoutMs = 30_000;

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

        udpClient = new UdpClient("sc.rpg.town", 7878);
        udpSendThread = new Thread(SendRemoteData);
        udpSendThread.IsBackground = true;
        udpReceiveThread = new Thread(ReceiveRemoteData);
        udpReceiveThread.IsBackground = true;
        sendTimer = Stopwatch.StartNew();
        syncing = true;
        udpSendThread.Start();
        udpReceiveThread.Start();
    }

    public void ReceiveRemoteData()
    {
        byte[] buf = new byte[MAX_RESPONSE_COUNT*(SIZE_CONTENTID+SIZE_SCALE)+SIZE_SEQ+SIZE_VERSION+SIZE_HANDLE];
        try
        {
            uint lastSeq = 0;

            while (syncing)
            {
                if (!Configuration.Sync) { Thread.Sleep(100); continue; }

                int len;
                int offset = 0;
                try
                {
                    len = udpClient.Client.Receive(buf);
                }
                catch (SocketException) { continue; }
                if(len < SIZE_CONTENTID+SIZE_SCALE+SIZE_SEQ+SIZE_VERSION+SIZE_HANDLE)
                {
                    Logger.Warning("Short packet received: {len}{buf}", len, buf);
                    continue;
                }

                ushort incomingVersion = BitConverter.ToUInt16(buf, offset);
                offset+= SIZE_VERSION;
                if(incomingVersion != VERSION)
                {
                    Logger.Error("Expected version 1 got {incomingVersion}", incomingVersion);
                    continue;
                }
                ushort incomingHandle = BitConverter.ToUInt16(buf, offset);
                offset += SIZE_HANDLE;
                if(incomingHandle == 0)
                {
                    Logger.Error("Expected nonzero handle got 0");
                    continue;
                }
                userHandle = incomingHandle;
                uint incomingPacketSeq = BitConverter.ToUInt32(buf, offset);
                offset += SIZE_SEQ;
                if(incomingPacketSeq > lastSeq)
                {
                    lastSeq = incomingPacketSeq;
                }
                else
                {
                    Logger.Info("Out of order packet received seq: {incomingPacketSeq}. Skipping.", incomingPacketSeq);
                    continue;
                }
                int numScales = (len - SIZE_VERSION - SIZE_SEQ)/(SIZE_SCALE+SIZE_CONTENTID);
                int scaleMapOffset = offset;
                SCContentIDScale[] scaleMapping = new SCContentIDScale[numScales];
                //TODO: Change this to a block copy once you've worked out how the struct layout works, if this turns out to suck for performance
                //Or to avoid bothering with struct layout just have two arrays - one for scale floats and one for contentids
                for (int i = 0; i < numScales; i++)
                {
                    scaleMapping[i].ContentId = BitConverter.ToUInt64(buf, offset+i*(SIZE_SCALE+SIZE_CONTENTID));
                    scaleMapping[i].Scale = BitConverter.ToSingle(buf, offset+i*(SIZE_SCALE+SIZE_CONTENTID)+SIZE_CONTENTID);
                    Logger.Info("ContentID {ContentId} has scale {Scale}", scaleMapping[i].ContentId, scaleMapping[i].Scale);
                    Logger.Info("numScales is {numScales}", numScales);
                }

                CharacterUpdates.Enqueue(scaleMapping);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Logger.Warning("receive thread: {msg}", ex.Message); }
    }

    public void SendRemoteData()
    {
        try
        {
            int len = SIZE_VERSION + SIZE_HANDLE + SIZE_SEQ + SIZE_SCALE + SIZE_CONTENTID;
            byte[] pkt = new byte[len];
            while (localContentId == 0L && syncing) 
            {
                Logger.Info("Waiting for localContentId to not be 0");
                Thread.Sleep(100);
            }
            if (!syncing) 
            {
                Logger.Warning("Not syncing.");
                return;
            }
            uint seq = 0;
            uint offset = 0;

            byte[] ver = BitConverter.GetBytes(VERSION);
            ver.CopyTo(pkt, 0);

            while (syncing)
            {
                offset = SIZE_VERSION;
                if (!Configuration.Sync) { Thread.Sleep(100); continue; }

                byte[] handleBytes = BitConverter.GetBytes(userHandle);
                handleBytes.CopyTo(pkt, offset);
                offset += SIZE_HANDLE;

                byte[] seqBytes = BitConverter.GetBytes(seq);
                seq++;
                seqBytes.CopyTo(pkt, offset);
                offset += SIZE_SEQ;

                byte[] contentIdBytes = BitConverter.GetBytes(localContentId);
                contentIdBytes.CopyTo(pkt, offset);
                offset += SIZE_CONTENTID;

                byte[] scaleBytes = BitConverter.GetBytes(localScale);
                scaleBytes.CopyTo(pkt, offset);

                Logger.Info("Sending packet with length {len} scale is {localScale} just FYI", len, localScale);
                udpClient.Send(pkt, len);

                int sleepTime = MS_PER_FRAME - (int)sendTimer.ElapsedMilliseconds;
                sendTimer.Restart();
                if (sleepTime > 0) Thread.Sleep(sleepTime);
            }
        }
        catch (ObjectDisposedException ex) { Logger.Warning(ex.Message); }
        catch (SocketException ex) { Logger.Warning(ex.Message); }
        catch (Exception ex) { Logger.Warning("send thread: {msg}", ex.Message); }
    }

    public void Dispose()
    {
        syncing = false;
        udpClient.Close();
        udpReceiveThread.Join(1000);
        udpSendThread.Join(1000);
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName_SizeChange);
        CommandManager.RemoveHandler(CommandName_Scale);
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
        if(ContentIdToCharacterScaleMap.ContainsKey(actor->ContentId))
        {
            charState = ContentIdToCharacterScaleMap[actor->ContentId];
        }
        else
        {
            charState = new SCCharacterState();
        }
        charState.PlayerScale = scale;
        var draw = (CharacterBase*)actor->DrawObject;
        var currentScale = draw->Scale.Y;
        charState.PreviousScale = currentScale;

        ContentIdToCharacterScaleMap[actor->ContentId] = charState;
    }
    
    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        bool disable = (ClientState.IsPvP || !Configuration.Enable|| (Configuration.OnlyActiveInCombat && !Condition[ConditionFlag.InCombat]));
        var player = ObjectTable.LocalPlayer;
        if (player == null) return;

        ulong localId = ((Character*)player.Address)->ContentId;
        while(CharacterUpdates.TryDequeue(out SCContentIDScale[]? scaleMapping))
        {
            foreach (SCContentIDScale scaleTuple in scaleMapping)
            {
                if(localId == scaleTuple.ContentId) continue;
                if (!ContentIdToCharacterScaleMap.TryGetValue(scaleTuple.ContentId, out var charState))
                    charState = new SCCharacterState { PreviousScale = 1.0f };
                charState.PlayerScale = scaleTuple.Scale;
                ContentIdToCharacterScaleMap[scaleTuple.ContentId] = charState;
            }
        }

        foreach (var actor in ObjectTable.PlayerObjects)
        {
            if (actor == null) continue;
            ulong actorContentId = ((Character*)actor.Address)->ContentId;
            bool isLocalPlayer = localId == actorContentId;

            AdjustScale((Character*)actor.Address, Configuration.GrowFromDamage, disable || (!isLocalPlayer && !Configuration.AlterAnyone), isLocalPlayer);
        }

        //So this is real stupid but it should work. The functionality of this plugin requires that it update after anything else that might set the scale
        //This should ensure that without costing too much performance, and as an added bonus I don't actually have to handle any IPC nonsense with Customize+
        //which seems like a complete nightmare. 
        Framework.Update -= OnFrameworkUpdate;
        Framework.Update += OnFrameworkUpdate;
    }

    // find the actor's health and shield value and uses that to adjust the model's scale
    public unsafe void AdjustScale(Character* actor, bool growFromDamage, bool disable, bool isLocalPlayer)
    {
        if (actor == null) return;
        float maxhp = actor->MaxHealth;
        float shield = (actor->ShieldValue / 100f) * maxhp;
        float health = actor->Health + shield;
        float hpRatio = health / maxhp;

        var draw = (CharacterBase*)actor->DrawObject;

        if (draw != null)
        {
            float scale = draw->Scale.Y;
            
            float previousScale = scale;
            SCCharacterState charState;
            if(ContentIdToCharacterScaleMap.ContainsKey(actor->ContentId))
            {
                charState = ContentIdToCharacterScaleMap[actor->ContentId];
                previousScale = charState.PreviousScale;
            }
            else
            {
                charState.PlayerScale = 1.0f;
            }
            if (previousScale != scale)
            {
                charState.PlayerScale = scale;
            }
            
            float targetScale = disable ? charState.PlayerScale : growFromDamage ? 
            Math.Clamp(Configuration.MaxScaleMultiplier - (Configuration.MaxScaleMultiplier * hpRatio), Configuration.MinScaleMultiplier, Configuration.MaxScaleMultiplier)*charState.PlayerScale : 
            Math.Clamp(hpRatio, Configuration.MinScaleMultiplier, float.PositiveInfinity)*charState.PlayerScale;
            scale = float.Lerp(previousScale, targetScale, Configuration.Speed);
            
            draw->Scale = new Vector3(scale, scale, scale);
            actor->Scale = scale;
            charState.PreviousScale = scale;
            ContentIdToCharacterScaleMap[actor->ContentId] = charState;
            if(isLocalPlayer)
            {
                localContentId = actor->ContentId;
                localScale = charState.PreviousScale;
            }
        }
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
