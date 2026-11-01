using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using UnityEngine.InputSystem;
using CodeZero.DDuck;
using Newtonsoft.Json;
using System.Threading.Tasks;
using UnityEngine.InputSystem.LowLevel;

/// <summary>Reproducible authoring of the native DDuck sandbox. Run via Pipeline script run.</summary>
public static class DDuckSetup
{
    const string Root = "Assets/Art/Characters/DDuck";
    const string ScenePath = "Assets/Art/Scenes/Test Scene.unity";
    [Serializable] public class Manifest { public int cellSize; public Animation[] animations; }
    [Serializable] public class Animation { public int id; public int durationRaw; public int[] frameIds; public string file; }
    static readonly Dictionary<int,string> Names = new Dictionary<int,string>
    {
        {1,"Idle"},{3,"Walk"},{4,"CapeDeflect"},{5,"Fire"},{6,"Crouch"},{7,"CrouchFire"},
        {8,"Stunned"},{9,"Hurt"},{0x0B,"AirStun"},{0x0C,"AirHurt"},{0x0D,"ReachJump"},
        {0x0E,"AirFireRising"},{0x0F,"AirFireFalling"},{0x11,"Jump"},{0x12,"ForcedHop"},
        {0x1C,"Shock"},{0x1D,"Slime"},{0x2B,"MissionIntro"},{0x2C,"CheckpointSpawn"},{0x2D,"CarriedExit"},
        {0x13,"GasProjectile"},{0x14,"GasBurst"},{0x18,"GasCloud"}
    };

    public static string Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before authoring.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new InvalidOperationException("Open the requested Test Scene first; no other scene will be modified.");
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(Root+"/Animations/DDuck.controller"))
            throw new InvalidOperationException("DDuck already authored. Build is for a fresh asset destination; use targeted edits to preserve authored changes.");
        foreach (var folder in new[]{"Animations","Input","Prefabs","Materials"}) Directory.CreateDirectory(Root+"/"+folder);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(Root+"/Sprites/manifest.json"));
        if (manifest.animations.Length != 23) throw new InvalidOperationException("Expected twenty actor and three projectile animations.");
        var clips = new Dictionary<int,AnimationClip>();
        foreach (var animation in manifest.animations)
        {
            var sprites = Slice(animation, manifest.cellSize);
            clips[animation.id] = Clip(animation.id, Names[animation.id], sprites, animation.durationRaw, false);
            if (animation.id == 3 || animation.id == 0x0D)
                Clip(animation.id, Names[animation.id]+"Loop", sprites.Skip(1).ToArray(), animation.durationRaw, true);
        }

        var actorController = ActorAnimator(clips);
        var projectileController = ProjectileAnimator(clips);
        var actions = InputActions();
        var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(Root+"/Materials/DDuck.physicsMaterial2D");
        if (!material)
        {
            material = new PhysicsMaterial2D("DDuck"){friction=0,bounciness=0};
            AssetDatabase.CreateAsset(material,Root+"/Materials/DDuck.physicsMaterial2D");
        }
        var projectile = new GameObject("DDuck Gas Projectile");
        projectile.layer = 2;
        var projectileBody = projectile.AddComponent<Rigidbody2D>();
        projectileBody.bodyType=RigidbodyType2D.Kinematic;
        projectileBody.gravityScale=0;
        projectileBody.useFullKinematicContacts=true;
        projectileBody.collisionDetectionMode=CollisionDetectionMode2D.Continuous;
        var projectileCollider = projectile.AddComponent<CircleCollider2D>();
        projectileCollider.radius=.2f;
        projectileCollider.isTrigger=true;
        var projectileVisual = Visual(projectile,projectileController,SpriteFor(clips[0x13]));
        var projectileScript = projectile.AddComponent<DDuckProjectile>();
        Assign(projectileScript,"animator",projectileVisual.GetComponent<Animator>());
        var projectileAsset = PrefabUtility.SaveAsPrefabAsset(projectile,Root+"/Prefabs/DDuck Gas Projectile.prefab");
        UnityEngine.Object.DestroyImmediate(projectile);

        var actor = new GameObject("DDuck");
        actor.layer=2;
        var body=actor.AddComponent<Rigidbody2D>();
        body.bodyType=RigidbodyType2D.Dynamic;
        body.constraints=RigidbodyConstraints2D.FreezeRotation;
        body.gravityScale=DDuckController.Gravity / Mathf.Abs(Physics2D.gravity.y);
        body.collisionDetectionMode=CollisionDetectionMode2D.Continuous;
        body.interpolation=RigidbodyInterpolation2D.Interpolate;
        body.sleepMode=RigidbodySleepMode2D.NeverSleep;
        var collider=actor.AddComponent<BoxCollider2D>();
        collider.size=new Vector2(1.375f,1.5f);
        collider.sharedMaterial=material;
        var joint=actor.AddComponent<FixedJoint2D>();
        joint.autoConfigureConnectedAnchor=false;
        joint.anchor=new Vector2(0,.875f);
        joint.enabled=false;
        var visual=Visual(actor,actorController,SpriteFor(clips[1]));
        var input=actor.AddComponent<PlayerInput>();
        input.actions=actions;
        input.defaultActionMap="Gameplay";
        input.notificationBehavior=PlayerNotifications.InvokeCSharpEvents;
        input.neverAutoSwitchControlSchemes=false;
        var controller=actor.AddComponent<DDuckController>();
        Assign(controller,"animator",visual.GetComponent<Animator>());
        Assign(controller,"spriteRenderer",visual.GetComponent<SpriteRenderer>());
        Assign(controller,"projectilePrefab",projectileAsset.GetComponent<DDuckProjectile>());
        var actorAsset=PrefabUtility.SaveAsPrefabAsset(actor,Root+"/Prefabs/DDuck.prefab");
        UnityEngine.Object.DestroyImmediate(actor);

        var old=scene.GetRootGameObjects().FirstOrDefault(x=>x.name=="DDuck Sandbox");
        if (old) UnityEngine.Object.DestroyImmediate(old);
        var sandbox=new GameObject("DDuck Sandbox");
        var floor=GameObject.Find("Square");
        if (!floor || !floor.GetComponent<BoxCollider2D>()) throw new InvalidOperationException("Original floor is absent.");
        float top=floor.GetComponent<BoxCollider2D>().bounds.max.y;
        var instance=(GameObject)PrefabUtility.InstantiatePrefab(actorAsset,scene);
        instance.transform.SetParent(sandbox.transform);
        instance.transform.position=new Vector3(-3,top+.8f,0);
        var floorSprite=floor.GetComponent<SpriteRenderer>().sprite;
        var hook=Block("Hook",new Vector2(-1.3f,top+3.3f),new Vector2(.35f,.22f),new Color(.92f,.72f,.25f),floorSprite,sandbox.transform,false);
        var trigger=hook.AddComponent<CircleCollider2D>();
        trigger.radius=.5f; // Uniform marker scale is not used for trigger range.
        hook.transform.localScale=Vector3.one;
        hook.GetComponent<SpriteRenderer>().drawMode=SpriteDrawMode.Sliced;
        hook.GetComponent<SpriteRenderer>().size=new Vector2(.35f,.22f);
        trigger.isTrigger=true;
        hook.AddComponent<DDuckHook>();
        Block("Platform",new Vector2(1.25f,top+1.6f),new Vector2(2f,.25f),new Color(.32f,.47f,.57f),floorSprite,sandbox.transform,true);
        var target=Block("Shooting Target",new Vector2(4.5f,top+.6f),new Vector2(.6f,1.2f),new Color(.85f,.37f,.22f),floorSprite,sandbox.transform,true);
        target.AddComponent<DDuckTarget>();
        var hazard=Block("Shock Pad",new Vector2(3f,top+.08f),new Vector2(.7f,.16f),new Color(.85f,.72f,.13f),floorSprite,sandbox.transform,true);
        hazard.GetComponent<BoxCollider2D>().isTrigger=true;
        var damage=hazard.AddComponent<DDuckHazard>();
        var data=new SerializedObject(damage);data.FindProperty("hurtAnimation").intValue=0x1C;data.ApplyModifiedPropertiesWithoutUndo();
        // Keep the user's camera, floor, render pipeline and global physics/input settings.
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Selection.activeGameObject=instance;
        return $"PASS: {manifest.animations.Length} source strips, {clips.Count+2} clips, actor/projectile prefabs, input asset; scene={scene.path}";
    }

    static Sprite[] Slice(Animation animation,int cell)
    {
        string path=Root+"/Sprites/"+animation.file;
        var importer=(TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType=TextureImporterType.Sprite;
        importer.spriteImportMode=SpriteImportMode.Multiple;
        importer.spritePixelsPerUnit=16;
        importer.filterMode=FilterMode.Point;
        importer.textureCompression=TextureImporterCompression.Uncompressed;
        importer.mipmapEnabled=false;
        importer.npotScale=TextureImporterNPOTScale.None;
        importer.alphaIsTransparency=true;
        importer.maxTextureSize=4096;
        importer.SaveAndReimport();
        var factory=new SpriteDataProviderFactories();factory.Init();
        var provider=factory.GetSpriteEditorDataProviderFromObject(importer);
        if (provider==null) throw new Exception("No sprite data provider: "+path);
        provider.InitSpriteEditorDataProvider();
        var capability=provider.GetDataProvider<ISpriteFrameEditCapability>();
        if (capability==null || !capability.GetEditCapability().HasCapability(EEditCapability.CreateAndDeleteSprite)
            || !capability.GetEditCapability().HasCapability(EEditCapability.EditSpriteRect)
            || !capability.GetEditCapability().HasCapability(EEditCapability.EditPivot))
            throw new Exception("Importer does not support slicing/pivot editing: "+path);
        var rects=new List<SpriteRect>();
        for(int i=0;i<animation.frameIds.Length;i++)
        {
            string name=$"A{animation.id:X2}_{i:D3}";
            string hash;
            using(var md5=MD5.Create()) hash=BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(path+name))).Replace("-","").ToLowerInvariant();
            rects.Add(new SpriteRect{name=name,rect=new Rect(i*cell,0,cell,cell),alignment=SpriteAlignment.Custom,pivot=new Vector2(.5f,.5f),spriteID=new GUID(hash)});
        }
        provider.SetSpriteRects(rects.ToArray());
        var ids=provider.GetDataProvider<ISpriteNameFileIdDataProvider>();
        if(ids==null) throw new Exception("No stable sprite file ID provider: "+path);
        ids.SetNameFileIdPairs(rects.Select(r=>new SpriteNameFileIdPair(r.name,r.spriteID)));
        provider.Apply();importer.SaveAndReimport();
        var sprites=AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().OrderBy(s=>s.name).ToArray();
        if(sprites.Length!=rects.Count) throw new Exception("Slicing count mismatch: "+path);
        return sprites;
    }

    public static string VerifyAssets()
    {
        var manifest=JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(Root+"/Sprites/manifest.json"));
        int frames=0;
        foreach(var a in manifest.animations)
        {
            var sprites=AssetDatabase.LoadAllAssetsAtPath(Root+"/Sprites/"+a.file).OfType<Sprite>().OrderBy(s=>s.name).ToArray();
            if(sprites.Length!=a.frameIds.Length)throw new Exception("Sprite count: "+a.file);
            if(sprites.Any(s=>s.rect.size!=new Vector2(48,48)||s.pivot!=new Vector2(24,24)||s.pixelsPerUnit!=16))throw new Exception("Sprite geometry: "+a.file);
            frames+=sprites.Length;
            var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>($"{Root}/Animations/{a.id:X2}_{Names[a.id]}.anim");
            if(!clip)throw new Exception("Missing clip: "+a.file);
            var keys=AnimationUtility.GetObjectReferenceCurve(clip,EditorCurveBinding.PPtrCurve("",typeof(SpriteRenderer),"m_Sprite"));
            if(keys.Length!=sprites.Length+1||keys.Any(k=>!k.value))throw new Exception("Clip references: "+a.file);
        }
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/DDuck.prefab");
        if(!prefab.GetComponent<DDuckController>()||!prefab.GetComponent<Rigidbody2D>()||!prefab.GetComponent<BoxCollider2D>())throw new Exception("Prefab components missing");
        var input=prefab.GetComponent<PlayerInput>();
        if(input.actions.FindActionMap("Gameplay").actions.Count!=4)throw new Exception("Input asset missing actions");
        var result=new {status="PASS",strips=manifest.animations.Length,frames,clips=25,scene=ScenePath,unity=Application.unityVersion};
        File.WriteAllText("Art/Temp/zero87/assets.json",JsonConvert.SerializeObject(result,Formatting.Indented));
        return JsonConvert.SerializeObject(result);
    }

    public static async Task<string> VerifyPlayMode()
    {
        if(!EditorApplication.isPlaying)throw new Exception("Enter Play mode first.");
        var actor=UnityEngine.Object.FindAnyObjectByType<DDuckController>();
        if(!actor)throw new Exception("DDuck instance missing");
        var input=actor.GetComponent<PlayerInput>();
        var originalDevices=input.devices.ToArray();
        var originalScheme=input.currentControlScheme;
        var oldBackground=InputSystem.settings.backgroundBehavior;
        var oldEditorInput=InputSystem.settings.editorInputBehaviorInPlayMode;
        // Devices created while an unfocused player is running can be disabled immediately.
        // Configure focus policy before AddDevice, and explicitly enable synthetic devices.
        InputSystem.settings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;
        InputSystem.settings.editorInputBehaviorInPlayMode=InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        var keyboard=InputSystem.AddDevice<Keyboard>("DDuckSmokeKeyboard");
        var gamepad=InputSystem.AddDevice<Gamepad>("DDuckSmokeGamepad");
        InputSystem.EnableDevice(keyboard);InputSystem.EnableDevice(gamepad);
        var checks=new List<object>();
        Action<bool,string,object> check=(ok,name,detail)=>
        {
            checks.Add(new {name,pass=ok,detail});
            if(!ok)throw new Exception(name+": "+JsonConvert.SerializeObject(detail));
        };
        // Let the normal player update consume edges once; an extra manual update can
        // clear WasPressedThisFrame before the actor's Update observes it.
        Action<Key[]> keys=pressed=>InputSystem.QueueStateEvent(keyboard,new KeyboardState(pressed));
        Func<int,Task> wait=milliseconds=>Task.Delay(milliseconds);
        var body=actor.Body;
        try
        {
            input.SwitchCurrentControlScheme("Keyboard",keyboard);
            check(keyboard.enabled&&gamepad.enabled,"synthetic devices enabled",new{keyboard=keyboard.enabled,gamepad=gamepad.enabled});
            keys(Array.Empty<Key>());
            actor.Respawn();
            int frame=Time.frameCount;
            await wait(650);
            check(Time.frameCount>frame+5,"player loop advances",new {first=frame,last=Time.frameCount});
            check(actor.Grounded,"lands on original floor",new {position=body.position.ToString(),actor.AnimationId});
            float start=body.position.x;
            keys(new[]{Key.RightArrow});await wait(220);
            check(body.position.x>start+.6f&&actor.FacingRight,"keyboard right movement",new{start,end=body.position.x,speed=body.linearVelocityX,actor.AnimationId,keyDown=keyboard.rightArrowKey.isPressed,action=input.actions.FindAction("Move").ReadValue<Vector2>().ToString()});
            keys(new[]{Key.LeftArrow});await wait(220);
            check(!actor.FacingRight&&body.linearVelocityX<0,"keyboard left and flip",body.linearVelocityX);
            keys(new[]{Key.DownArrow});await wait(100);
            check(actor.Crouched&&actor.AnimationId==6,"crouch collider and pose",actor.GetComponent<BoxCollider2D>().size.ToString());
            keys(new[]{Key.DownArrow,Key.X});await wait(80);
            check(actor.ActiveShots>0&&actor.AnimationId==7,"crouch fire",new{actor.ActiveShots,actor.AnimationId});
            keys(new[]{Key.UpArrow});await wait(250);
            check(actor.IsDeflecting&&actor.AnimationId==4,"cape deflect",actor.AnimationId);
            keys(Array.Empty<Key>());actor.Respawn();await wait(600);
            body.position=new Vector2(-4.4f,body.position.y);
            float baseY=body.position.y,peak=baseY;
            keys(new[]{Key.Space});
            for(int i=0;i<16;i++){await wait(25);peak=Mathf.Max(peak,body.position.y);}
            keys(Array.Empty<Key>());await wait(500);
            check(peak-baseY>2.2f&&peak-baseY<3.5f&&actor.Grounded,"full jump and landing",new{height=peak-baseY,actor.Grounded});
            float shortBase=body.position.y,shortPeak=shortBase;
            keys(new[]{Key.Space});await wait(75);keys(Array.Empty<Key>());
            for(int i=0;i<12;i++){await wait(25);shortPeak=Mathf.Max(shortPeak,body.position.y);}
            check(shortPeak-shortBase<peak-baseY-.6f,"release cuts jump",new{fullHeight=peak-baseY,shortHeight=shortPeak-shortBase});
            await wait(350);
            var peg=UnityEngine.Object.FindAnyObjectByType<DDuckHook>();
            body.position=peg.Anchor-new Vector2(0,1.15f);body.linearVelocity=new Vector2(0,-.1f);
            await wait(200);
            check(actor.Hanging&&actor.GetComponent<FixedJoint2D>().enabled,"automatic hook catch uses joint",actor.Hanging);
            keys(new[]{Key.Space});await wait(80);
            check(!actor.Hanging&&body.linearVelocityY>0,"jump releases hook",body.linearVelocityY);
            keys(Array.Empty<Key>());actor.Respawn();await wait(600);
            foreach(var p in UnityEngine.Object.FindObjectsByType<DDuckProjectile>())UnityEngine.Object.Destroy(p.gameObject);
            await wait(50);
            // Stay left of the shock pad's contact range while firing at the target.
            body.position=new Vector2(-.9f,body.position.y);
            keys(new[]{Key.RightArrow});await wait(30);keys(Array.Empty<Key>());await wait(30);
            var target=UnityEngine.Object.FindAnyObjectByType<DDuckTarget>();int hits=target.Hits;
            check(actor.Health==4,"target fixture avoids hazard",actor.Health);
            keys(new[]{Key.X});await wait(450);keys(Array.Empty<Key>());
            check(target.Hits>hits,"gas projectile hits target",new{before=hits,after=target.Hits,actor.Health,actor.ActiveShots,actor.FacingRight});
            body.position=new Vector2(-4.3f,body.position.y);await wait(80);
            actor.ReceiveHit(body.position+Vector2.right,0x1C);
            int hp=actor.Health;actor.ReceiveHit(body.position+Vector2.right);
            await wait(60);
            check(hp==3&&actor.Health==3&&actor.AnimationId==0x1C,"shock and damage immunity",new{hp=actor.Health,actor.AnimationId});
            await wait(1100);actor.ReceiveHit(body.position+Vector2.right,0x1D);await wait(60);
            check(actor.Health==2&&actor.AnimationId==0x1D,"slime artwork context",new{actor.Health,actor.AnimationId});
            actor.Respawn();await wait(600);
            input.SwitchCurrentControlScheme("Gamepad",gamepad);
            float gx=body.position.x;
            InputSystem.QueueStateEvent(gamepad,new GamepadState{leftStick=Vector2.right});await wait(180);
            check(body.position.x>gx+.4f,"gamepad stick movement",new{start=gx,end=body.position.x});
            InputSystem.QueueStateEvent(gamepad,new GamepadState().WithButton(GamepadButton.South));await wait(100);
            check(body.linearVelocityY>0&&!actor.Grounded,"gamepad jump",body.linearVelocityY);
            InputSystem.QueueStateEvent(gamepad,new GamepadState());actor.Defeat();await wait(80);
            check(actor.Health==0&&!actor.GetComponent<BoxCollider2D>().enabled,"defeat",actor.Health);
            await wait(2200);check(actor.Health==4&&actor.GetComponent<BoxCollider2D>().enabled,"automatic respawn",actor.Health);
            var result=new{status="PASS",checks,frameCount=Time.frameCount};
            var json=JsonConvert.SerializeObject(result,Formatting.Indented);
            File.WriteAllText("Art/Temp/zero87/playmode.json",json);
            return json;
        }
        catch(Exception ex)
        {
            File.WriteAllText("Art/Temp/zero87/playmode.json",JsonConvert.SerializeObject(new{status="FAIL",checks,error=ex.Message},Formatting.Indented));
            throw;
        }
        finally
        {
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.QueueStateEvent(gamepad,new GamepadState());
            if(originalDevices.Length>0)input.SwitchCurrentControlScheme(originalScheme,originalDevices);
            InputSystem.RemoveDevice(keyboard);InputSystem.RemoveDevice(gamepad);
            InputSystem.settings.backgroundBehavior=oldBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode=oldEditorInput;
            actor.Respawn();
        }
    }

    static AnimationClip Clip(int id,string name,Sprite[] sprites,int ticks,bool loop)
    {
        string path=$"{Root}/Animations/{id:X2}_{name}.anim";
        var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if(!clip){clip=new AnimationClip{name=name};AssetDatabase.CreateAsset(clip,path);}
        clip.frameRate=60;
        var keys=new List<ObjectReferenceKeyframe>();
        for(int i=0;i<sprites.Length;i++) keys.Add(new ObjectReferenceKeyframe{time=i*ticks/60f,value=sprites[i]});
        keys.Add(new ObjectReferenceKeyframe{time=sprites.Length*ticks/60f,value=sprites[sprites.Length-1]});
        AnimationUtility.SetObjectReferenceCurve(clip,EditorCurveBinding.PPtrCurve("",typeof(SpriteRenderer),"m_Sprite"),keys.ToArray());
        var settings=AnimationUtility.GetAnimationClipSettings(clip);settings.loopTime=loop;
        AnimationUtility.SetAnimationClipSettings(clip,settings);
        EditorUtility.SetDirty(clip);return clip;
    }

    static AnimatorController ActorAnimator(Dictionary<int,AnimationClip> clips)
    {
        string path=Root+"/Animations/DDuck.controller";
        var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if(controller) throw new InvalidOperationException("DDuck controller already exists. Use targeted edits; Build never replaces authored controllers.");
        controller=AnimatorController.CreateAnimatorControllerAtPath(path);
        controller.AddParameter("AnimationId",AnimatorControllerParameterType.Int);
        controller.AddParameter("ChangePose",AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Grounded",AnimatorControllerParameterType.Bool);
        controller.AddParameter("Speed",AnimatorControllerParameterType.Float);
        controller.AddParameter("VerticalSpeed",AnimatorControllerParameterType.Float);
        var machine=controller.layers[0].stateMachine;
        int index=0;
        foreach(var pair in clips.Where(p=>p.Key!=0x13&&p.Key!=0x14&&p.Key!=0x18))
        {
            var state=machine.AddState(Names[pair.Key],new Vector3((index%4)*240,(index/4)*100));index++;
            state.motion=pair.Value;state.writeDefaultValues=false;
            if(pair.Key==1) machine.defaultState=state;
            var transition=machine.AddAnyStateTransition(state);
            transition.duration=0;transition.hasExitTime=false;transition.canTransitionToSelf=false;
            transition.AddCondition(AnimatorConditionMode.Equals,pair.Key,"AnimationId");
            transition.AddCondition(AnimatorConditionMode.If,0,"ChangePose");
            if(pair.Key==3||pair.Key==0x0D)
            {
                var loop=machine.AddState(Names[pair.Key]+"Loop",new Vector3(1000,index*75));
                loop.motion=AssetDatabase.LoadAssetAtPath<AnimationClip>($"{Root}/Animations/{pair.Key:X2}_{Names[pair.Key]}Loop.anim");
                loop.writeDefaultValues=false;
                var exit=state.AddTransition(loop);exit.hasExitTime=true;exit.exitTime=1;exit.duration=0;
            }
        }
        EditorUtility.SetDirty(controller);return controller;
    }

    static AnimatorController ProjectileAnimator(Dictionary<int,AnimationClip> clips)
    {
        var controller=AnimatorController.CreateAnimatorControllerAtPath(Root+"/Animations/DDuck Gas.controller");
        controller.AddParameter("Burst",AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Cloud",AnimatorControllerParameterType.Bool);
        var machine=controller.layers[0].stateMachine;
        var fly=machine.AddState("GasProjectile");fly.motion=clips[0x13];
        var cloud=machine.AddState("GasCloud");cloud.motion=clips[0x18];
        var burst=machine.AddState("GasBurst");burst.motion=clips[0x14];
        var advance=fly.AddTransition(cloud);advance.duration=0;advance.hasExitTime=false;advance.AddCondition(AnimatorConditionMode.If,0,"Cloud");
        var end=machine.AddAnyStateTransition(burst);end.duration=0;end.hasExitTime=false;end.canTransitionToSelf=false;end.AddCondition(AnimatorConditionMode.If,0,"Burst");
        return controller;
    }

    static InputActionAsset InputActions()
    {
        string path=Root+"/Input/DDuck.inputactions";
        var asset=ScriptableObject.CreateInstance<InputActionAsset>();
        var map=asset.AddActionMap("Gameplay");
        var move=map.AddAction("Move",InputActionType.Value,expectedControlLayout:"Vector2");
        move.AddCompositeBinding("2DVector").With("Up","<Keyboard>/w","Keyboard").With("Down","<Keyboard>/s","Keyboard").With("Left","<Keyboard>/a","Keyboard").With("Right","<Keyboard>/d","Keyboard");
        move.AddCompositeBinding("2DVector").With("Up","<Keyboard>/upArrow","Keyboard").With("Down","<Keyboard>/downArrow","Keyboard").With("Left","<Keyboard>/leftArrow","Keyboard").With("Right","<Keyboard>/rightArrow","Keyboard");
        move.AddBinding("<Gamepad>/dpad",groups:"Gamepad");move.AddBinding("<Gamepad>/leftStick",groups:"Gamepad");
        var jump=map.AddAction("Jump",InputActionType.Button,expectedControlLayout:"Button");
        jump.AddBinding("<Keyboard>/space",groups:"Keyboard");jump.AddBinding("<Keyboard>/z",groups:"Keyboard");jump.AddBinding("<Gamepad>/buttonSouth",groups:"Gamepad");
        var fire=map.AddAction("Fire",InputActionType.Button,expectedControlLayout:"Button");
        fire.AddBinding("<Keyboard>/x",groups:"Keyboard");fire.AddBinding("<Keyboard>/j",groups:"Keyboard");fire.AddBinding("<Gamepad>/buttonWest",groups:"Gamepad");
        var restart=map.AddAction("Restart",InputActionType.Button,expectedControlLayout:"Button");
        restart.AddBinding("<Keyboard>/r",groups:"Keyboard");restart.AddBinding("<Gamepad>/start",groups:"Gamepad");
        asset.AddControlScheme("Keyboard").WithRequiredDevice("<Keyboard>");
        asset.AddControlScheme("Gamepad").WithRequiredDevice("<Gamepad>");
        File.WriteAllText(path,asset.ToJson());UnityEngine.Object.DestroyImmediate(asset);
        AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
    }

    static GameObject Visual(GameObject parent,RuntimeAnimatorController controller,Sprite sprite)
    {
        var visual=new GameObject("Visual");visual.transform.SetParent(parent.transform,false);
        var renderer=visual.AddComponent<SpriteRenderer>();renderer.sprite=sprite;renderer.sortingOrder=10;
        var shader=Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        string path=Root+"/Materials/DDuck Sprite.mat";
        var material=AssetDatabase.LoadAssetAtPath<Material>(path);
        if(!material){material=new Material(shader);AssetDatabase.CreateAsset(material,path);}
        renderer.sharedMaterial=material;
        var animator=visual.AddComponent<Animator>();animator.runtimeAnimatorController=controller;
        animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        return visual;
    }
    static Sprite SpriteFor(AnimationClip clip) => (Sprite)AnimationUtility.GetObjectReferenceCurve(clip,EditorCurveBinding.PPtrCurve("",typeof(SpriteRenderer),"m_Sprite"))[0].value;
    static void Assign(UnityEngine.Object target,string name,UnityEngine.Object value)
    {var data=new SerializedObject(target);data.FindProperty(name).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
    static GameObject Block(string name,Vector2 position,Vector2 size,Color color,Sprite sprite,Transform parent,bool solid)
    {
        var go=new GameObject(name);go.transform.SetParent(parent);go.transform.position=position;go.transform.localScale=size;
        var visual=go.AddComponent<SpriteRenderer>();visual.sprite=sprite;visual.color=color;
        if(solid)go.AddComponent<BoxCollider2D>();return go;
    }
}
