using System;
using System.IO;
using System.Linq;
using Darkwing.Core;
using Darkwing.UnityApp;
using UnityEngine;

/// <summary>Ordinary scene route: buttons only, no RAM seed, direct mission entry or phase writes.</summary>
public static class BootFlowProbe
{
    const string Root = "D:/Art/SW Code Zero Unity";
    static byte[] Data(string name) => File.ReadAllBytes(Root + "/Assets/Darkwing/Data/" + name + ".bytes");

    public static string Prepare()
    {
        var inputs = new System.Collections.Generic.List<DarkwingLauncher.ReplayInput>();
        foreach (int f in new[] {180, 380, 580}) inputs.Add(Input(f, f+3, 0x10));
        inputs.Add(Input(780, 783, 0x40));
        foreach (int f in new[] {1080, 1280, 1480, 1680, 1880}) inputs.Add(Input(f, f+3, 0x80));
        inputs.Add(Input(2700, 2703, 0x40));
        inputs.Add(Input(2780, 2850, 1));
        inputs.Add(Input(2870, 2885, 0x80));
        inputs.Add(Input(2930, 2933, 0x40));
        foreach (string host in new[] {"editor", "player"})
        {
            var spec = new DarkwingLauncher.ReplaySpec {frames=3150, inputs=inputs.ToArray(),
                captures=new[] {80, 700, 1000, 2680, 2710, 2800, 2900, 3000, 3150},
                output=Root + "/Temp/" + host + "_route"};
            File.WriteAllText(Root + "/Temp/route_input_" + host + ".json", JsonUtility.ToJson(spec));
        }
        return "Prepared Editor/player button schedules in Temp (no game state seed)";
    }
    static DarkwingLauncher.ReplayInput Input(int first, int last, int buttons) =>
        new DarkwingLauncher.ReplayInput {first=first, last=last, buttons=buttons};

    public static string EditorRoute()
    {
        Prepare();
        UnityEditor.SessionState.SetString("DarkwingReplay", Root + "/Temp/route_input_editor.json");
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/DarkwingMain.unity");
        UnityEditor.EditorApplication.isPlaying = true;
        return "Queued buttons-only scene replay from Awake";
    }
    public static string Progress()
    {
        var app = UnityEngine.Object.FindFirstObjectByType<DarkwingLauncher>();
        return app == null ? "No live launcher" : $"frame={app.Game.Frame}, phase={app.Flow.Stage}, finished={app.ReplayFinished}";
    }

    public static string Verify()
    {
        var editor = Read("editor"); var player = Read("player");
        if (!editor.editor || player.editor || editor.states.Length != player.states.Length)
            throw new Exception("Wrong host or incomplete route results");
        for (int i=0; i<editor.states.Length; i++)
            if (JsonUtility.ToJson(editor.states[i]) != JsonUtility.ToJson(player.states[i]))
                throw new Exception("Editor/player divergence at capture " + i);
        var states = editor.states;
        foreach (string phase in new[] {"Title", "NewGameDialog", "MapCursor", "MapBriefing", "Mission"})
            if (!states.Any(s => s.phase == phase)) throw new Exception("Route skipped " + phase);
        var idle=states.Single(s => s.frame==2680); var fire=states.Single(s => s.frame==2710);
        var walk=states.Single(s => s.frame==2800); var jump=states.Single(s => s.frame==2900);
        if (idle.phase != "Mission" || idle.world != 0 || idle.chr0 != 0 || idle.chr1 != 16
            || walk.x <= idle.x || jump.y >= idle.y || !fire.slots.Contains("04:13/01"))
            throw new Exception("Mission entry or movement/jump/fire failed");
        return $"PASS: {states.Length} Editor/player states byte-identical (framebuffer/NT/palette/OAM/slots); " +
            $"mission0 CHR0/16; walk X {idle.x:X4}->{walk.x:X4}; jump Y {idle.y:X4}->{jump.y:X4}; gas slot04 anim13/behavior01.";
    }
    static DarkwingLauncher.ReplayResult Read(string host) => JsonUtility.FromJson<DarkwingLauncher.ReplayResult>(
        File.ReadAllText(Root + "/Temp/" + host + "_route/route.json"));

    public static string IdleTitle()
    {
        var data=DataLoader.Load(Data("chr_full"),Data("levelsets"),Data("screens"),Data("levels"),Data("anims"));
        var decoder=new RoomDecoder(Data("prg_bank0"),Data("prg_bank1"),Data("world_bank7"),Data("levelsets"));
        var game=new DarkwingGame(data) {Bank5=Data("prg_bank5"),Bank7=Data("world_bank7"),Bank2=Data("prg_bank2")};
        game.Ppu.WriteRegister(0x2000,0x90); game.Ppu.WriteRegister(0x2001,0x1E);
        var flow=new GameFlow(game,decoder); var pad=default(NesButtons);
        for (int f=0; f<600; f++) {flow.Step(in pad); game.Step(in pad);}
        if (flow.Stage!=GameFlow.Phase.Title || game.Ppu.Chr0Bank!=31 || game.Ppu.Chr1Bank!=25)
            throw new Exception("Idle title took the RESET intro timer branch");
        return "PASS: title remains title for 600 no-input frames, CHR31/25; no world pre-load.";
    }
}
