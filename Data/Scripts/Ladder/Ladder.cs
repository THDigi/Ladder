using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Sandbox.Common;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Common.Utils;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ObjectBuilders;
using VRage.Game.Entity;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using Digi.Utils;
using VRageRender;
using Ingame = Sandbox.ModAPI.Ingame;
using IMyControllableEntity = Sandbox.Game.Entities.IMyControllableEntity;

namespace Digi.Ladder
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class LadderMod : MySessionComponentBase
    {
        private bool init = false;
        private bool isDedicated = false;
        
        private IMyEntity character = null;
        //private MyCharacterDefinition characterDef = null;
        private IMyTerminalBlock usingLadder = null;
        private float mounting = 2f;
        private float dismounting = 2;
        private MyOrientedBoundingBoxD ladderBox = new MyOrientedBoundingBoxD();
        private Vector3 gravity = Vector3.Zero;
        private short skipPlanets = 0;
        private byte skipRetryGravity = GRAVITY_UPDATERATE;
        private byte skipRefreshAnim = 0;
        private bool alignedToGravity = false;
        private float travelForSound = 0;
        private bool grabOnLoad = false;
        
        public Settings settings = null;
        
        //private MyEntity debugBox = null; // UNDONE DEBUG
        
        private const string LEARNFILE = "learned";
        private bool loadedAllLearned = false;
        private bool[] learned = new bool[5];
        private IMyHudNotification[] learnNotify = new MyHudNotification[5];
        private string[] learnText = new string[]
        {
            "Move forward/backward to climb",
            "Sprint to climb/descend faster",
            "Strafe left/right to dismount left or right",
            "Jump to jump off in the direction you're looking",
            "Crouch or turn on jetpack to dismount"
        };
        
        private MyEntity3DSoundEmitter soundEmitter = null;
        
        public static readonly MySoundPair soundStep = new MySoundPair("PlayerLadderStep");
        
        public static Dictionary<long, MyPlanet> planets = new Dictionary<long, MyPlanet>();
        public static Dictionary<long, IMyGravityGeneratorBase> gravityGenerators = new Dictionary<long, IMyGravityGeneratorBase>();
        
        public static IMyHudNotification status;
        public static Dictionary<long, IMyTerminalBlock> ladders = new Dictionary<long, IMyTerminalBlock>();
        
        private HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
        private List<IMyPlayer> players = new List<IMyPlayer>();
        
        public const float ALIGN_STEP = 0.01f;
        public const float ALIGN_MUL = 1.2f;
        public const float TICKRATE = 1f/60f;
        public const float UPDATE_RADIUS = 10f;
        public const float EXTRA_OFFSET_Z = 0.5f;
        public const double RAY_HEIGHT = 1.7;
        
        public const byte GRAVITY_UPDATERATE = 15;
        
        public const ushort PACKET = 23991;
        public const int PACKET_RANGE_SQ = 100*100;
        
        public static readonly Encoding encode = Encoding.Unicode;
        
        public const string LEARN_UNCHECK = "[  ] ";
        public const string LEARN_CHECK = "[x] ";
        public static readonly MyStringId CUSTOMTEXT = MyStringId.GetOrCompute("{0}");
        
        private LadderAnimation lastLadderAnim = LadderAnimation.NONE;
        
        public enum LadderAnimation
        {
            NONE = 0,
            MOUNTING,
            IDLE,
            UP,
            DOWN,
            DISMOUNT_LEFT,
            DISMOUNT_RIGHT,
            JUMP_OFF,
        }
        
        public static string[] ladderAnimations = new string[]
        {
            null,
            "Mod_LadderMounting",
            "Mod_LadderIdle",
            "Mod_LadderUp",
            "Mod_LadderDown",
            "Mod_LadderDismountLeft",
            "Mod_LadderDismountRight",
            "Mod_LadderJumpOff",
        };
        
        public void Init()
        {
            init = true;
            isDedicated = MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Utilities.IsDedicated;
            
            Log.Init();
            Log.Info("Initialized.");
            
            if(!isDedicated)
            {
                settings = new Settings();
                
                if(settings != null && settings.onLadderWorlds.Count > 0 && settings.onLadderWorlds.Contains(MyAPIGateway.Session.Name.ToLower()))
                {
                    grabOnLoad = true;
                }
                
                MyAPIGateway.Utilities.MessageEntered += MessageEntered;
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET, ReceivedPacket);
                
                try
                {
                    if(MyAPIGateway.Utilities.FileExistsInLocalStorage(LEARNFILE, typeof(LadderMod)))
                    {
                        var file = MyAPIGateway.Utilities.ReadFileInLocalStorage(LEARNFILE, typeof(LadderMod));
                        
                        string line;
                        
                        while((line = file.ReadLine()) != null)
                        {
                            line = line.Trim();
                            
                            if(line.StartsWith("//"))
                                continue;
                            
                            switch(line)
                            {
                                case "move_climb":
                                    learned[0] = true;
                                    break;
                                case "move_sprint":
                                    learned[1] = true;
                                    break;
                                case "side_dismount":
                                    learned[2] = true;
                                    break;
                                case "jump_dismount":
                                    learned[3] = true;
                                    break;
                                case "crouch_dismount":
                                    learned[4] = true;
                                    break;
                            }
                        }
                        
                        loadedAllLearned = (learned[0] && learned[1] && learned[2] && learned[3] && learned[4]);
                        
                        file.Close();
                    }
                }
                catch(Exception e)
                {
                    Log.Error(e);
                }
            }
        }
        
        private void SaveLearn()
        {
            if(loadedAllLearned)
                return;
            
            try
            {
                StringBuilder text = new StringBuilder();
                
                if(learned[0])
                    text.AppendLine("move_climb");
                
                if(learned[1])
                    text.AppendLine("move_sprint");
                
                if(learned[2])
                    text.AppendLine("side_dismount");
                
                if(learned[3])
                    text.AppendLine("jump_dismount");
                
                if(learned[4])
                    text.AppendLine("crouch_dismount");
                
                if(text.Length > 0)
                {
                    var file = MyAPIGateway.Utilities.WriteFileInLocalStorage(LEARNFILE, typeof(LadderMod));
                    file.Write(text.ToString());
                    file.Flush();
                    file.Close();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public override void SaveData()
        {
            try
            {
                if(!isDedicated)
                {
                    if(settings == null)
                    {
                        Log.Error("SaveData() :: Settings didn't initialize!");
                        return;
                    }
                    
                    string worldName = MyAPIGateway.Session.Name.ToLower();
                    
                    if(usingLadder != null != settings.onLadderWorlds.Contains(worldName))
                    {
                        if(usingLadder != null)
                            settings.onLadderWorlds.Add(worldName);
                        else
                            settings.onLadderWorlds.Remove(worldName);
                        
                        settings.Save();
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        protected override void UnloadData()
        {
            try
            {
                init = false;
                ladders.Clear();
                planets.Clear();
                gravityGenerators.Clear();
                
                if(!isDedicated)
                {
                    if(settings != null)
                    {
                        settings.Close();
                        settings = null;
                    }
                    
                    MyAPIGateway.Utilities.MessageEntered -= MessageEntered;
                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET, ReceivedPacket);
                    
                    SaveLearn();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            Log.Info("Mod unloaded.");
            Log.Close();
        }
        
        public void ReceivedPacket(byte[] bytes)
        {
            try
            {
                string data = encode.GetString(bytes);
                long entId;
                
                if(long.TryParse(data, out entId) && MyAPIGateway.Entities.EntityExists(entId))
                {
                    var ent = MyAPIGateway.Entities.GetEntityById(entId);
                    var emitter = new MyEntity3DSoundEmitter(ent as MyEntity);
                    emitter.PlaySound(soundStep, false, false, false);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public static void SetLadderStatus(string text, MyFontEnum font, int aliveTime = 100)
        {
            if(status == null)
            {
                status = MyAPIGateway.Utilities.CreateNotification(text, aliveTime, font);
            }
            else
            {
                status.Font = font;
                status.Text = text;
                status.AliveTime = aliveTime;
                status.Show();
            }
        }
        
        private void UpdatePlanets()
        {
            planets.Clear();
            MyAPIGateway.Entities.GetEntities(ents, delegate(IMyEntity e)
                                              {
                                                  if(e is MyPlanet)
                                                  {
                                                      if(!planets.ContainsKey(e.EntityId))
                                                          planets.Add(e.EntityId, e as MyPlanet);
                                                  }
                                                  
                                                  return false; // no reason to add to the list
                                              });
        }
        
        public Vector3 GetGravityVector(Vector3D point)
        {
            var artificialDir = Vector3.Zero;
            var naturalDir = Vector3.Zero;
            
            try
            {
                foreach(var kv in planets)
                {
                    var planet = kv.Value;
                    
                    if(planet.Closed || planet.MarkedForClose)
                        continue;
                    
                    var dir = planet.PositionComp.GetPosition() - point;
                    
                    if(dir.LengthSquared() <= planet.GravityLimitSq)
                    {
                        dir.Normalize();
                        naturalDir += dir * planet.GetGravityMultiplier(point);
                    }
                }
                
                foreach(var generator in gravityGenerators.Values)
                {
                    if(generator.IsWorking)
                    {
                        if(generator is IMyGravityGeneratorSphere)
                        {
                            var gen = (generator as IMyGravityGeneratorSphere);
                            
                            if(Vector3D.DistanceSquared(generator.WorldMatrix.Translation, point) <= (gen.Radius * gen.Radius))
                            {
                                var dir = generator.WorldMatrix.Translation - point;
                                dir.Normalize();
                                artificialDir += (Vector3)dir * (gen.Gravity / 9.81f); // HACK remove division once gravity value is fixed
                            }
                        }
                        else if(generator is IMyGravityGenerator)
                        {
                            var gen = (generator as IMyGravityGenerator);
                            var halfExtents = new Vector3(gen.FieldWidth / 2, gen.FieldHeight / 2, gen.FieldDepth / 2);
                            var box = new MyOrientedBoundingBoxD(gen.WorldMatrix.Translation, halfExtents, Quaternion.CreateFromRotationMatrix(gen.WorldMatrix));
                            
                            if(box.Contains(ref point))
                            {
                                artificialDir += gen.WorldMatrix.Down * gen.Gravity;
                            }
                        }
                    }
                }
                
                float mul = MathHelper.Clamp(1f - naturalDir.Length() * 2f, 0f, 1f);
                artificialDir *= mul;
                
                return (naturalDir + artificialDir);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            return Vector3.Zero;
        }
        
        public override void UpdateBeforeSimulation()
        {
            try
            {
                if(isDedicated)
                    return;
                
                if(!init)
                {
                    if(MyAPIGateway.Session == null)
                        return;
                    
                    Init();
                    
                    if(isDedicated)
                        return;
                }
                
                var playerControlled = MyAPIGateway.Session.ControlledObject;
                
                /* TODO ?
                if(character != null)
                {
                    if(character.Closed)
                    {
                        character = null;
                    }
                    else if(playerControlled.Entity is IMyCharacter && character.EntityId != playerControlled.Entity.EntityId)
                    {
                        character = null;
                    }
                }
                
                if(character == null && playerControlled.Entity is IMyCharacter)
                {
                    character = playerControlled.Entity;
                    
                    var obj = character.GetObjectBuilder(false) as MyObjectBuilder_Character;
                    MyDefinitionManager.Static.Characters.TryGetValue(obj.CharacterModel, out characterDef);
                }
                 */
                
                if(playerControlled != null)
                {
                    if(playerControlled.Entity is IMyCharacter)
                    {
                        character = playerControlled.Entity;
                    }
                    else if(playerControlled.Entity is Ingame.IMyCockpit) // in a seat, certainly not gonna climb ladders
                    {
                        character = null;
                    }
                    // other cases depend on having the character controlled for a bit to get the reference
                    else if(character != null && character.Closed)
                    {
                        character = null;
                    }
                }
                else
                {
                    character = null;
                }
                
                if(character != null)
                {
                    if(++skipPlanets >= 600)
                    {
                        skipPlanets = 0;
                        UpdatePlanets();
                    }
                    
                    var charCtrl = character as IMyControllableEntity;
                    bool controllingCharacter = (playerControlled != null && playerControlled.Entity is IMyCharacter);
                    IMyTerminalBlock ladder = null;
                    MyCubeBlock ladderInternal = null;
                    
                    if(soundEmitter == null || soundEmitter.Entity == null || soundEmitter.Entity.EntityId != charCtrl.Entity.EntityId)
                    {
                        if(soundEmitter != null)
                            soundEmitter.StopSound(true, true);
                        
                        soundEmitter = new MyEntity3DSoundEmitter(charCtrl as MyEntity);
                    }
                    
                    MatrixD ladderMatrix = character.WorldMatrix; // temporarily using it for character matrix, then used for ladder matrix
                    var charPos = ladderMatrix.Translation + ladderMatrix.Up * 0.05;
                    var charPos2 = ladderMatrix.Translation + ladderMatrix.Up * RAY_HEIGHT;
                    var charRay = new RayD(charPos, ladderMatrix.Up);
                    
                    if(dismounting <= 1) // relative top dismount sequence
                    {
                        if(usingLadder == null)
                        {
                            dismounting = 2;
                            return;
                        }
                        
                        ladder = usingLadder;
                        ladderInternal = ladder as MyCubeBlock;
                        dismounting *= ALIGN_MUL;
                        
                        ladderMatrix = ladder.WorldMatrix;
                        var charOnLadder = ladderMatrix.Translation + ladderMatrix.Forward * (ladderInternal.BlockDefinition.ModelOffset.Z + EXTRA_OFFSET_Z);
                        
                        if(ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                            charOnLadder += ladderMatrix.Backward;
                        
                        var topDir = Vector3D.Dot(character.WorldMatrix.Up, ladderMatrix.Up);
                        var matrix = character.WorldMatrix;
                        var halfY = ((ladderInternal.BlockDefinition.Size.Y * ladder.CubeGrid.GridSize) / 2);
                        matrix.Translation = charOnLadder + (topDir > 0 ? ladderMatrix.Up : ladderMatrix.Down) * (halfY + 0.1f) + ladderMatrix.Backward * 0.75;
                        
                        character.SetWorldMatrix(MatrixD.SlerpScale(character.WorldMatrix, matrix, MathHelper.Clamp(dismounting, 0.0f, 1.0f)));
                        
                        character.Physics.LinearVelocity = ladder.CubeGrid.Physics.GetVelocityAtPoint(character.WorldMatrix.Translation); // sync velocity with the ladder
                        
                        SetLadderStatus("Dismounting ladder...", MyFontEnum.White);
                        
                        travelForSound += 3f;
                        ComputeSound(charPos, 30);
                        
                        if(dismounting > 1f)
                            ExitLadder(false);
                        
                        return;
                    }
                    
                    // UNDONE DEBUG
                    //{
                    //    var c = Color.Blue.ToVector4();
                    //    MySimpleObjectDraw.DrawLine(charPos, charPos2, "WeaponLaserIgnoreDepth", ref c, 0.5f);
                    //}
                    
                    // find a ladder
                    foreach(var l in ladders.Values)
                    {
                        if(l.Closed || l.MarkedForClose || !l.IsFunctional)
                            continue;
                        
                        if(Vector3D.DistanceSquared(l.WorldMatrix.Translation, charPos) <= UPDATE_RADIUS)
                        {
                            ladderInternal = l as MyCubeBlock;
                            ladderMatrix = l.WorldMatrix;
                            
                            // update ladder oriented box to find character in it accurately
                            Quaternion.CreateFromRotationMatrix(ref ladderMatrix, out ladderBox.Orientation);
                            
                            if(l is MyAdvancedDoor && !(l as MyAdvancedDoor).FullyOpen)
                            {
                                ladderBox.HalfExtent = (ladderInternal.BlockDefinition.Size * l.CubeGrid.GridSize) / 2;
                                
                                var offset = ladderInternal.BlockDefinition.ModelOffset;
                                ladderBox.Center = ladderMatrix.Translation + ladderMatrix.Up * 1.125f + ladderMatrix.Forward * (offset.Z + EXTRA_OFFSET_Z);
                                
                                ladderBox.HalfExtent.Y = 0.25;
                                ladderBox.HalfExtent.Z = 0.5;
                                
                                if(l.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                                    ladderBox.Center += ladderMatrix.Backward;
                            }
                            else
                            {
                                ladderBox.HalfExtent = (ladderInternal.BlockDefinition.Size * l.CubeGrid.GridSize) / 2;
                                ladderBox.HalfExtent.Z = 0.5;
                                
                                var offset = ladderInternal.BlockDefinition.ModelOffset;
                                ladderBox.Center = ladderMatrix.Translation + ladderMatrix.Forward * (offset.Z + EXTRA_OFFSET_Z);
                                
                                if(l.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                                    ladderBox.Center += ladderMatrix.Backward;
                            }
                            
                            if(!ladderBox.Contains(ref charPos) && !ladderBox.Contains(ref charPos2))
                            {
                                var intersect = ladderBox.Intersects(ref charRay);
                                
                                if(!intersect.HasValue || intersect.Value < 0 || intersect.Value > RAY_HEIGHT)
                                    continue;
                            }
                            
                            // UNDONE DEBUG
                            //{
                            //    {
                            //        var c = Color.Red.ToVector4();
                            //        MySimpleObjectDraw.DrawLine(ladderBox.Center + ladderMatrix.Down * ladderBox.HalfExtent.Y, ladderBox.Center + ladderMatrix.Up * ladderBox.HalfExtent.Y, "WeaponLaserIgnoreDepth", ref c, 0.01f);
                            //    }
                            //
                            //    if(debugBox == null)
                            //    {
                            //        debugBox = new MyEntity();
                            //        debugBox.Init(null, @"Models\Debug\Error.mwm", null, null, null);
                            //        debugBox.PositionComp.LocalMatrix = Matrix.Identity;
                            //        debugBox.Flags = EntityFlags.Visible | EntityFlags.NeedsDraw | EntityFlags.NeedsDrawFromParent | EntityFlags.InvalidateOnMove;
                            //        debugBox.OnAddedToScene(null);
                            //        debugBox.Render.Transparency = 0.5f;
                            //        debugBox.Render.RemoveRenderObjects();
                            //        debugBox.Render.AddRenderObjects();
                            //    }
                            //    var matrix = MatrixD.CreateWorld(ladderBox.Center, ladderMatrix.Forward, ladderMatrix.Up);
                            //    var scale = ladderBox.HalfExtent * 2;
                            //    MatrixD.Rescale(ref matrix, ref scale);
                            //    debugBox.PositionComp.SetWorldMatrix(matrix);
                            //}
                            
                            ladder = l;
                            ladderInternal = l as MyCubeBlock;
                            break;
                        }
                    }
                    
                    if(ladder != null)
                    {
                        var offset = ladderInternal.BlockDefinition.ModelOffset;
                        var charOnLadder = ladderMatrix.Translation + ladderMatrix.Forward * (offset.Z + EXTRA_OFFSET_Z);
                        
                        if(ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                            charOnLadder += ladderMatrix.Backward;
                        
                        if(++skipRetryGravity >= GRAVITY_UPDATERATE)
                        {
                            skipRetryGravity = 0;
                            alignedToGravity = true;
                            gravity = GetGravityVector(character.WorldMatrix.Translation);
                            
                            if(gravity.LengthSquared() > 0)
                            {
                                float gravDot = Vector3.Dot(Vector3D.Normalize(gravity), ladderMatrix.Down);
                                
                                if(!(gravDot >= 0.9f || gravDot <= -0.9f))
                                {
                                    alignedToGravity = false;
                                }
                            }
                        }
                        
                        if(!alignedToGravity)
                        {
                            SetLadderStatus("Gravity not parallel to ladder!", MyFontEnum.Red, 2000);
                            
                            if(usingLadder != null)
                                ExitLadder(false);
                            
                            return;
                        }
                        
                        bool readInput = MyGuiScreenGamePlay.ActiveGameplayScreen == null;
                        
                        if(usingLadder == null) // first ladder interaction
                        {
                            //var controlUse = MyAPIGateway.Input.GetGameControl(MyControlsSpace.USE);
                            //
                            //if(!controlUse.IsPressed())
                            //{
                            //    string assigned = (controlUse.GetKeyboardControl() != MyKeys.None ? MyAPIGateway.Input.GetKeyName(controlUse.GetKeyboardControl()) : (controlUse.GetMouseControl() != MyMouseButtonsEnum.None ? MyAPIGateway.Input.GetName(controlUse.GetMouseControl()) : "(NONE)")) + (controlUse.GetSecondKeyboardControl() != MyKeys.None ? " or " + MyAPIGateway.Input.GetKeyName(controlUse.GetSecondKeyboardControl()) : null);
                            //    SetLadderStatus("Press "+assigned+" to use the ladder.", MyFontEnum.White);
                            //    return;
                            //}
                            
                            if(grabOnLoad)
                            {
                                grabOnLoad = false;
                            }
                            else
                            {
                                if(settings.useLadder1 == null && settings.useLadder2 == null)
                                {
                                    SetLadderStatus("Ladder interaction is unassigned! Edit the settings.cfg file!", MyFontEnum.Red);
                                    return;
                                }
                                
                                bool use1 = (settings.useLadder1 != null ? readInput && settings.useLadder1.IsPressed() : false);
                                bool use2 = (settings.useLadder2 != null ? readInput && settings.useLadder2.IsPressed() : false);
                                
                                if(!use1 && !use2)
                                {
                                    StringBuilder assigned = new StringBuilder();
                                    
                                    if(settings.useLadder1 != null)
                                        assigned.Append(settings.useLadder1.GetFriendlyString());
                                    
                                    if(settings.useLadder2 != null)
                                    {
                                        if(assigned.Length > 0)
                                            assigned.Append(" or ");
                                        
                                        assigned.Append(settings.useLadder2.GetFriendlyString());
                                    }
                                    
                                    SetLadderStatus("Press "+assigned+" to use the ladder.", MyFontEnum.White);
                                    return;
                                }
                            }
                            
                            skipRefreshAnim = 60;
                            travelForSound = 0;
                            mounting = (controllingCharacter ? ALIGN_STEP : 2);
                            
                            LadderAnim(character, LadderAnimation.MOUNTING);
                        }
                        
                        if(usingLadder != ladder)
                            usingLadder = ladder;
                        
                        if(charCtrl.Entity.Physics == null)
                        {
                            ExitLadder(false);
                            return;
                        }
                        
                        if(readInput)
                        {
                            var controlCrouch = MyAPIGateway.Input.GetGameControl(MyControlsSpace.CROUCH);
                            
                            if(controlCrouch.IsPressed())
                            {
                                if(!learned[4])
                                    learned[4] = true;
                                
                                ExitLadder(false);
                                return;
                            }
                        }
                        
                        character.Physics.LinearVelocity = ladder.CubeGrid.Physics.GetVelocityAtPoint(character.WorldMatrix.Translation); // sync velocity with the ladder
                        
                        if(skipRefreshAnim > 0 && --skipRefreshAnim == 0) // force refresh animation after mounting due to an issue
                        {
                            var anim = lastLadderAnim;
                            lastLadderAnim = LadderAnimation.NONE;
                            LadderAnim(character, anim);
                        }
                        
                        if(mounting <= 1) // mounting on ladder sequence
                        {
                            mounting *= ALIGN_MUL;
                            
                            float align = Vector3.Dot(ladderMatrix.Up, character.WorldMatrix.Up);
                            
                            var matrix = MatrixD.CreateFromDir(ladderMatrix.Backward, (align > 0 ? ladderMatrix.Up : ladderMatrix.Down));
                            
                            var vC = character.WorldMatrix.Translation;
                            vC = vC - (ladderMatrix.Forward * Vector3D.Dot(vC, ladderMatrix.Forward)) - (ladderMatrix.Left * Vector3D.Dot(vC, ladderMatrix.Left));
                            var vL = charOnLadder - (ladderMatrix.Up * Vector3D.Dot(charOnLadder, ladderMatrix.Up));
                            matrix.Translation = vL + vC;
                            
                            character.SetWorldMatrix(MatrixD.SlerpScale(character.WorldMatrix, matrix, MathHelper.Clamp(mounting, 0.0f, 1.0f)));
                            
                            if(mounting >= 0.75f && charCtrl.EnabledThrusts) // delayed turning off thrusts because gravity aligns you faster and can make you fail to attach to the ladder
                                charCtrl.SwitchThrusts();
                            
                            travelForSound += 3f;
                            ComputeSound(charPos, 30);
                            
                            SetLadderStatus("Mounting ladder...", MyFontEnum.White);
                            return;
                        }
                        
                        // TODO jetpack assited climb/descend ?
                        // TODO gravity assisted descend ?
                        
                        if(charCtrl.EnabledThrusts)
                        {
                            if(!learned[4])
                                learned[4] = true;
                            
                            ExitLadder(false); // leave ladder if jetpack is turned on
                            return;
                        }
                        
                        if(!controllingCharacter) // disable ladder control if not controlling character
                            readInput = false;
                        
                        bool movingAway = false;
                        float move = readInput ? MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.FORWARD) - MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.BACKWARD) : 0;
                        float side = readInput ? MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.STRAFE_RIGHT) - MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.STRAFE_LEFT) : 0;
                        var alignVertical = ladderMatrix.Up.Dot(character.WorldMatrix.Up);
                        
                        if(!loadedAllLearned)
                        {
                            bool allLearned = (learned[0] && learned[1] && learned[2] && learned[3] && learned[4]);
                            
                            for(int i = 0; i < learned.Length; i++)
                            {
                                if(learnNotify[i] == null)
                                    learnNotify[i] = new MyHudNotification(CUSTOMTEXT, 100, MyFontEnum.White, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, 5, MyNotificationLevel.Important);
                                
                                learnNotify[i].Text = (learned[i] ? LEARN_CHECK : LEARN_UNCHECK)+learnText[i];
                                learnNotify[i].Font = (learned[i] ? MyFontEnum.White : MyFontEnum.DarkBlue);
                                learnNotify[i].AliveTime = (allLearned ? 1000 : 100);
                                learnNotify[i].Show();
                            }
                            
                            if(allLearned)
                            {
                                SaveLearn();
                                loadedAllLearned = true;
                            }
                        }
                        
                        if(readInput && MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.JUMP))
                        {
                            character.Physics.LinearVelocity += character.WorldMatrix.Forward * 300 * TICKRATE;
                            
                            LadderAnim(character, LadderAnimation.JUMP_OFF);
                            
                            if(!learned[3])
                                learned[3] = true;
                            
                            travelForSound = 1;
                            ComputeSound(charPos, 0);
                            
                            ExitLadder(false);
                            return;
                        }
                        else if(side != 0)
                        {
                            var alignForward = ladderMatrix.Backward.Dot(character.WorldMatrix.Forward);
                            
                            if(alignForward < 0)
                                side = -side;
                            
                            character.Physics.LinearVelocity += side * (alignVertical > 0 ? ladderMatrix.Left : ladderMatrix.Right) * 100 * TICKRATE;
                            travelForSound += (float)Math.Round(Math.Abs(side) * 100 * TICKRATE, 5);
                            
                            LadderAnim(character, (side > 0 ? LadderAnimation.DISMOUNT_LEFT : LadderAnimation.DISMOUNT_RIGHT));
                            movingAway = true;
                            
                            if(!learned[2])
                                learned[2] = true;
                        }
                        else
                        {
                            // aligning player to ladder
                            Vector3 dir = charOnLadder - charPos;
                            Vector3 vel = dir - (ladderMatrix.Up * Vector3D.Dot(dir, ladderMatrix.Up)); // projecting up/down direction to ignore it
                            
                            if(vel.LengthSquared() >= 0.005f)
                            {
                                character.Physics.LinearVelocity += vel * 100 * TICKRATE;
                                travelForSound += (float)Math.Round(Math.Abs(side) * 100 * TICKRATE, 5);
                            }
                        }
                        
                        var view = MyAPIGateway.Session.ControlledObject.GetHeadMatrix(false, true);
                        float lookVertical = Vector3.Dot(character.WorldMatrix.Up, view.Forward);
                        float verticalModifier = MathHelper.Clamp((lookVertical + 0.65f) / 0.5f, -0.5f, 1.0f);
                        
                        if(verticalModifier < 0)
                            verticalModifier *= 2;
                        
                        move = (float)Math.Round(move * verticalModifier, 3);
                        
                        if(move != 0)
                        {
                            if(!learned[0])
                                learned[0] = true;
                            
                            var halfY = ((ladderInternal.BlockDefinition.Size.Y * ladder.CubeGrid.GridSize) / 2);
                            var edge = charOnLadder + ((alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * halfY);
                            
                            // climb over at the end when climbing up
                            if(move > 0 && Vector3D.DistanceSquared(charPos, edge) <= (0.25 * 0.25))
                            {
                                var nextBlockWorldPos = ladderMatrix.Translation + ladderMatrix.Forward * offset.Z + ((alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * (halfY + 0.1f));
                                var nextBlockPos = ladder.CubeGrid.WorldToGridInteger(nextBlockWorldPos);
                                var slim = ladder.CubeGrid.GetCubeBlock(nextBlockPos);
                                
                                if(slim == null || !(slim.FatBlock is IMyTerminalBlock) || !LadderBlock.ladderIds.Contains(slim.FatBlock.BlockDefinition.SubtypeId))
                                {
                                    dismounting = ALIGN_STEP;
                                    travelForSound = 0;
                                    return;
                                }
                            }
                            
                            // on the floor and moving backwards makes you dismount
                            if(move < 0 && MyHud.CharacterInfo.State == MyHudCharacterStateEnum.Standing)
                            {
                                travelForSound = 1;
                                ComputeSound(charPos, 0);
                                ExitLadder(false);
                                return;
                            }
                            
                            if(move != 0)
                            {
                                // TODO check if character has jetpack
                                // TODO use character definition stats?
                                
                                float speed = 120f + (MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.SPRINT) * 80f);
                                
                                if(!learned[1] && speed > 140)
                                    learned[1] = true;
                                
                                character.Physics.LinearVelocity += (alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * move * speed * TICKRATE;
                                
                                travelForSound += (float)Math.Round(Math.Abs(move) * speed * TICKRATE, 5);
                                
                                if(!movingAway)
                                    LadderAnim(character, (move > 0 ? LadderAnimation.UP : LadderAnimation.DOWN));
                            }
                        }
                        else if(!movingAway)
                        {
                            LadderAnim(character, LadderAnimation.IDLE);
                        }
                        
                        ComputeSound(charPos, movingAway ? 60 : 45);
                        return;
                    }
                }
                
                ExitLadder();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        private void ComputeSound(Vector3D position, int targetTick = 45)
        {
            if(travelForSound >= targetTick)
            {
                travelForSound = 0;
                soundEmitter.PlaySound(soundStep, true, false, false);
                
                var myId = MyAPIGateway.Multiplayer.MyId;
                var bytes = encode.GetBytes(character.EntityId.ToString());
                MyAPIGateway.Players.GetPlayers(players, delegate(IMyPlayer p)
                                                {
                                                    if(p.SteamUserId != myId && Vector3D.DistanceSquared(p.GetPosition(), position) <= PACKET_RANGE_SQ)
                                                    {
                                                        MyAPIGateway.Multiplayer.SendMessageTo(PACKET, bytes, p.SteamUserId, false);
                                                    }
                                                    
                                                    return false;
                                                });
            }
        }
        
        private void ExitLadder(bool setFreeFallAnimation = true)
        {
            if(grabOnLoad)
                grabOnLoad = false;
            
            if(usingLadder == null)
                return;
            
            usingLadder = null;
            alignedToGravity = false;
            skipRetryGravity = GRAVITY_UPDATERATE;
            
            if(MyAPIGateway.Session.ControlledObject is IMyCharacter && setFreeFallAnimation && character != null && lastLadderAnim != LadderAnimation.NONE)
            {
                LadderAnim(character, LadderAnimation.JUMP_OFF);
            }
            
            lastLadderAnim = LadderAnimation.NONE;
        }
        
        private void LadderAnim(IMyEntity ent, LadderAnimation anim)
        {
            if(lastLadderAnim == anim)
                return;
            
            lastLadderAnim = anim;
            var skinned = ent as MySkinnedEntity;
            
            skinned.AddCommand(new MyAnimationCommand()
                               {
                                   AnimationSubtypeName = ladderAnimations[(int)anim],
                                   FrameOption = MyFrameOption.Loop,
                                   PlaybackCommand = MyPlaybackCommand.Play,
                                   TimeScale = (anim == LadderAnimation.MOUNTING ? 1.5f : 1f),
                                   KeepContinuingAnimations = false,
                                   BlendOption = MyBlendOption.Immediate,
                                   BlendTime = 0.1f,
                               }, true);
        }
        
        public void MessageEntered(string msg, ref bool send)
        {
            try
            {
                if(msg.StartsWith("/ladder", StringComparison.InvariantCultureIgnoreCase))
                {
                    send = false;
                    msg = msg.Substring("/ladder".Length).Trim().ToLower();
                    
                    if(msg.Equals("reload"))
                    {
                        if(settings.Load())
                            MyAPIGateway.Utilities.ShowMessage(Log.MOD_NAME, "Reloaded and re-saved config.");
                        else
                            MyAPIGateway.Utilities.ShowMessage(Log.MOD_NAME, "Config created with the current settings.");
                        
                        settings.Save();
                        return;
                    }
                    
                    MyAPIGateway.Utilities.ShowMessage(Log.MOD_NAME, "Available commands:");
                    MyAPIGateway.Utilities.ShowMessage("/ladder reload ", "reloads the config file.");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_AdvancedDoor), "LargeShipUsableLadderRetractable", "SmallShipUsableLadderRetractable")]
    public class LadderRetractable : LadderLogic { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TerminalBlock), "LargeShipUsableLadder", "SmallShipUsableLadder", "SmallShipUsableLadderSegment")]
    public class LadderBlock : LadderLogic { }

    public class LadderLogic : MyGameLogicComponent
    {
        public static readonly HashSet<string> ladderIds = new HashSet<string>()
        {
            "LargeShipUsableLadder",
            "SmallShipUsableLadder",
            "SmallShipUsableLadderSegment",
            "LargeShipUsableLadderRetractable",
            "SmallShipUsableLadderRetractable"
        };
        
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }
        
        public override void UpdateOnceBeforeFrame()
        {
            var block = Entity as IMyTerminalBlock;
            
            if(block.CubeGrid.Physics == null)
                return;
            
            if(block.BlockDefinition.TypeId != typeof(MyObjectBuilder_AdvancedDoor))
            {
                block.SetValueBool("ShowInTerminal", false);
                block.SetValueBool("ShowInToolbarConfig", false);
            }
            
            LadderMod.ladders.Add(Entity.EntityId, block);
        }
        
        public override void Close()
        {
            LadderMod.ladders.Remove(Entity.EntityId);
        }
        
        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGenerator))]
    public class GravityGeneratorFlat : GravityGeneratorLogic { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGeneratorSphere))]
    public class GravityGeneratorSphere : GravityGeneratorLogic { }

    public class GravityGeneratorLogic : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }
        
        public override void UpdateOnceBeforeFrame()
        {
            var block = Entity as IMyGravityGeneratorBase;
            
            if(block.CubeGrid.Physics == null)
                return;
            
            LadderMod.gravityGenerators.Add(block.EntityId, block);
        }
        
        public override void Close()
        {
            LadderMod.gravityGenerators.Remove(Entity.EntityId);
        }
        
        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }
    }
}
