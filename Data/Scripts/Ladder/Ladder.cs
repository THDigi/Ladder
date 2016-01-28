using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Sandbox.Common;
using Sandbox.Common.Components;
using Sandbox.Common.Input;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.VRageData;
using Sandbox.Definitions;
using Sandbox.Engine;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Common.Utils;
using VRage.Compiler;
using VRage.Input;
using VRage.ObjectBuilders;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using Digi.Utils;
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
        private IMyTerminalBlock usingLadder = null;
        private bool controlMode = true;
        private bool initialDampeners = false;
        private float mounting = 2f;
        private float dismounting = 2;
        private MyOrientedBoundingBoxD ladderBox = new MyOrientedBoundingBoxD();
        private Vector3 gravity = Vector3.Zero;
        private byte skipPlanets = 0;
        private byte skipRetryGravity = 0;
        private byte skipRefreshAnim = 0;
        
        private const string LEARNFILE = "learned";
        private bool loadedAllLearned = false;
        private bool[] learned = new bool[4];
        private IMyHudNotification[] learnNotify = new MyHudNotification[4];
        private string[] learnText = new string[]
        {
            "Look up/down to climb",
            "Look left/right to dismount left/right",
            "Look back to jump off",
            "Dampeners key toggles control"
        };
        
        public static Dictionary<long, MyPlanet> planets = new Dictionary<long, MyPlanet>();
        public static Dictionary<long, Ingame.IMyGravityGeneratorBase> gravityGenerators = new Dictionary<long, Ingame.IMyGravityGeneratorBase>();
        
        public static IMyHudNotification status;
        public static Dictionary<long, IMyTerminalBlock> ladders = new Dictionary<long, IMyTerminalBlock>();
        
        private HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
        
        public const float ALIGN_STEP = 0.01f;
        public const float ALIGN_MUL = 1.2f;
        public const float TICKRATE = 1f/60f;
        public const float UPDATE_RADIUS = 5f;
        public static readonly Vector3D BOX_LARGE = new Vector3D(2.5, 2.5, 1) / 2;
        public static readonly Vector3D BOX_SMALL = new Vector3D(1.5, 2.5, 1) / 2;
        
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
            
            Log.Info("Initialized.");
            
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
                            case "climb":
                                learned[0] = true;
                                break;
                            case "dismount":
                                learned[1] = true;
                                break;
                            case "jump":
                                learned[2] = true;
                                break;
                            case "toggle":
                                learned[3] = true;
                                break;
                        }
                    }
                    
                    loadedAllLearned = (learned[0] && learned[1] && learned[2] && learned[3]);
                    
                    file.Close();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
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
                    text.AppendLine("climb");
                
                if(learned[1])
                    text.AppendLine("dismount");
                
                if(learned[2])
                    text.AppendLine("jump");
                
                if(learned[3])
                    text.AppendLine("toggle");
                
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
        
        protected override void UnloadData()
        {
            init = false;
            ladders.Clear();
            planets.Clear();
            gravityGenerators.Clear();
            
            SaveLearn();
            
            Log.Info("Mod unloaded.");
            Log.Close();
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
                        if(generator is Ingame.IMyGravityGeneratorSphere)
                        {
                            var gen = (generator as Ingame.IMyGravityGeneratorSphere);
                            
                            if(Vector3D.DistanceSquared(generator.WorldMatrix.Translation, point) <= (gen.Radius * gen.Radius))
                            {
                                var dir = generator.WorldMatrix.Translation - point;
                                dir.Normalize();
                                artificialDir += (Vector3)dir * (gen.Gravity / 9.81f); // HACK remove division once gravity value is fixed
                            }
                        }
                        else if(generator is Ingame.IMyGravityGenerator)
                        {
                            var gen = (generator as Ingame.IMyGravityGenerator);
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
        
        //private MyEntity debugBox = null; // UNDONE DEBUG
        
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
                
                if(character == null || character.Closed || character.MarkedForClose)
                {
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
                    }
                    else
                    {
                        character = null;
                    }
                }
                
                if(character != null)
                {
                    var charCtrl = character as IMyControllableEntity;
                    
                    if(charCtrl.EnabledThrusts || charCtrl.Entity.Physics == null)
                    {
                        ExitLadder(false); // leave ladder if jetpack is turned on
                        return;
                    }
                    
                    if(++skipPlanets >= 180)
                    {
                        skipPlanets = 0;
                        UpdatePlanets();
                    }
                    
                    bool controllingCharacter = (playerControlled != null && playerControlled.Entity is IMyCharacter);
                    
                    MatrixD ladderMatrix = character.WorldMatrix; // temporarily using it for character matrix, then used for ladder matrix
                    
                    // this is set below the feet to not catch the ladder when walking by it and to have the jump-over functionality at the end of the ladder top
                    var charPos = ladderMatrix.Translation + ladderMatrix.Down * 0.05;
                    
                    IMyTerminalBlock ladder = null;
                    
                    if(dismounting <= 1) // relative top dismount sequence
                    {
                        if(usingLadder == null)
                        {
                            dismounting = 2;
                            return;
                        }
                        
                        ladder = usingLadder;
                        dismounting *= ALIGN_MUL;
                        
                        ladderMatrix = ladder.WorldMatrix;
                        var charOnLadder = ladderMatrix.Translation + ladderMatrix.Backward * (ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large ? 0.55 : 0.4);
                        
                        var topDir = Vector3D.Dot(character.WorldMatrix.Up, ladderMatrix.Up);
                        var matrix = character.WorldMatrix;
                        matrix.Translation = charOnLadder + (topDir > 0 ? ladderMatrix.Up : ladderMatrix.Down) * 1.275f + ladderMatrix.Backward * 0.75;
                        
                        character.SetWorldMatrix(MatrixD.SlerpScale(character.WorldMatrix, matrix, MathHelper.Clamp(dismounting, 0.0f, 1.0f)));
                        
                        character.Physics.LinearVelocity = ladder.CubeGrid.Physics.GetVelocityAtPoint(character.WorldMatrix.Translation); // sync velocity with the ladder
                        
                        SetLadderStatus("Dismounting ladder...", MyFontEnum.White);
                        
                        if(dismounting > 1f)
                            ExitLadder(false);
                        
                        return;
                    }
                    
                    // find a ladder
                    foreach(var l in ladders.Values)
                    {
                        if(l.Closed || l.MarkedForClose || !l.IsFunctional)
                            continue;
                        
                        if(Vector3D.DistanceSquared(l.WorldMatrix.Translation, charPos) <= UPDATE_RADIUS)
                        {
                            ladderMatrix = l.WorldMatrix;
                            
                            // update ladder oriented box to find character in it accurately
                            Quaternion.CreateFromRotationMatrix(ref ladderMatrix, out ladderBox.Orientation);
                            ladderBox.HalfExtent = (l.CubeGrid.GridSizeEnum == MyCubeSize.Large ? BOX_LARGE : BOX_SMALL);
                            ladderBox.Center = ladderMatrix.Translation + ladderMatrix.Backward * (l.CubeGrid.GridSizeEnum == MyCubeSize.Large ? 0.75 : 0.4);
                            
                            if(ladderBox.Contains(ref charPos))
                            {
                                // UNDONE DEBUG
                                //if(debugBox == null)
                                //{
                                //    debugBox = new MyEntity();
                                //    debugBox.Init(null, @"Models\Debug\Error.mwm", null, null, null);
                                //    debugBox.PositionComp.LocalMatrix = Matrix.Identity;
                                //    debugBox.Flags = EntityFlags.Visible | EntityFlags.NeedsDraw | EntityFlags.NeedsDrawFromParent | EntityFlags.InvalidateOnMove;
                                //    debugBox.OnAddedToScene(null);
                                //}
                                //var matrix = MatrixD.CreateWorld(ladderBox.Center, ladderMatrix.Forward, ladderMatrix.Up);
                                //var scale = ladderBox.HalfExtent * 2;
                                //MatrixD.Rescale(ref matrix, ref scale);
                                //debugBox.PositionComp.SetWorldMatrix(matrix);
                                
                                ladder = l;
                                break;
                            }
                        }
                    }
                    
                    if(ladder != null)
                    {
                        var charOnLadder = ladderMatrix.Translation + ladderMatrix.Backward * (ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large ? 0.55 : 0.4);
                        
                        if(usingLadder == null) // first ladder interaction
                        {
                            if(skipRetryGravity > 0)
                            {
                                if(--skipRetryGravity > 0)
                                    return;
                            }
                            
                            gravity = GetGravityVector(character.WorldMatrix.Translation);
                            
                            if(gravity.LengthSquared() > 0)
                            {
                                float gravDot = Vector3.Dot(Vector3D.Normalize(gravity), ladderMatrix.Down);
                                
                                if(!(gravDot >= 0.9f || gravDot <= -0.9f))
                                {
                                    skipRetryGravity = 15; // re-check gravity every this amount of ticks
                                    SetLadderStatus("Gravity not parallel to ladder!", MyFontEnum.Red, 300);
                                    return;
                                }
                            }
                            
                            skipRefreshAnim = 60;
                            
                            mounting = (controllingCharacter ? ALIGN_STEP : 2);
                            
                            initialDampeners = charCtrl.EnabledDamping;
                            controlMode = controllingCharacter;
                            
                            LadderAnim(character, LadderAnimation.MOUNTING);
                        }
                        
                        if(usingLadder != ladder)
                            usingLadder = ladder;
                        
                        if(charCtrl.EnabledDamping != initialDampeners)
                        {
                            charCtrl.SwitchDamping();
                            controlMode = !controlMode;
                            
                            SetLadderStatus("Ladder control: "+(controlMode ? "On" : "Off"), MyFontEnum.White, 2000);
                            
                            if(!learned[3])
                                learned[3] = true;
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
                            
                            SetLadderStatus("Mounting ladder...", MyFontEnum.White);
                            return;
                        }
                        
                        var view = MyAPIGateway.Session.ControlledObject.GetHeadMatrix(false, true);
                        float alignSide = Vector3.Dot(ladderMatrix.Backward, view.Forward);
                        float alignVertical = Vector3.Dot(ladderMatrix.Up, view.Forward);
                        
                        bool movingAway = false;
                        
                        if(!loadedAllLearned)
                        {
                            bool allLearned = (learned[0] && learned[1] && learned[2] && learned[3]);
                            
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
                        
                        if(!controllingCharacter) // disable ladder control if not controlling character
                            controlMode = false;
                        
                        if(controlMode)
                        {
                            if(alignVertical >= 0.8f || alignVertical <= -0.8f) // ignore left/right if looking up/down really far
                                alignSide = 1;
                            
                            if(alignSide < 0.12f)
                            {
                                float sideDot = Vector3.Dot(view.Forward, ladderMatrix.Left);
                                
                                if(alignSide <= -0.8) // aim away from ladder to jump
                                {
                                    character.Physics.LinearVelocity += ladderMatrix.Forward * 300 * TICKRATE;
                                    
                                    LadderAnim(character, LadderAnimation.JUMP_OFF);
                                    movingAway = true;
                                    
                                    if(!learned[2])
                                        learned[2] = true;
                                }
                                else // aim left/right to dismount
                                {
                                    character.Physics.LinearVelocity += (sideDot > 0 ? ladderMatrix.Left : ladderMatrix.Right) * 200 * TICKRATE;
                                    
                                    LadderAnim(character, (sideDot > 0 ? LadderAnimation.DISMOUNT_LEFT : LadderAnimation.DISMOUNT_RIGHT));
                                    movingAway = true;
                                    
                                    if(!learned[1])
                                        learned[1] = true;
                                }
                            }
                            else // not dismounting left/right, centering
                            {
                                Vector3 dir = charOnLadder - charPos;
                                Vector3 vel = dir - (ladderMatrix.Up * Vector3D.Dot(dir, ladderMatrix.Up)); // projecting up/down direction to ignore it
                                
                                if(vel.LengthSquared() >= 0.005f)
                                {
                                    character.Physics.LinearVelocity += vel * 300 * TICKRATE;
                                }
                            }
                            
                            // climb relative up/down or dismount relative up
                            if(alignVertical > 0.3f || alignVertical < -0.3f)
                            {
                                var edge = charOnLadder + ((alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * 1.25f);
                                float lookUp = Vector3.Dot(character.WorldMatrix.Up, view.Forward);
                                
                                // climb over at the end when climbing up
                                if(lookUp > 0 && Vector3D.DistanceSquared(charPos, edge) <= (0.25 * 0.25))
                                {
                                    var ladderInternal = ladder as MyCubeBlock;
                                    var offset = -Vector3.TransformNormal(ladderInternal.BlockDefinition.ModelOffset, ladderMatrix); // WorldMatrix.Translation takes ModelOffset into account
                                    var nextBlockWorldPos = ladderMatrix.Translation + offset + ((alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * 1.3f);
                                    var nextBlockPos = ladder.CubeGrid.WorldToGridInteger(nextBlockWorldPos);
                                    var slim = ladder.CubeGrid.GetCubeBlock(nextBlockPos);
                                    
                                    if(slim == null || !(slim.FatBlock is IMyTerminalBlock) || !LadderBlock.ladderIds.Contains(slim.FatBlock.BlockDefinition.SubtypeId))
                                    {
                                        dismounting = ALIGN_STEP;
                                        return;
                                    }
                                }
                                
                                character.Physics.LinearVelocity += (alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * 200 * TICKRATE;
                                
                                if(!movingAway)
                                    LadderAnim(character, (lookUp > 0 ? LadderAnimation.UP : LadderAnimation.DOWN));
                                
                                if(!learned[0])
                                    learned[0] = true;
                            }
                            else // standing
                            {
                                if(!movingAway)
                                    LadderAnim(character, LadderAnimation.IDLE);
                            }
                        }
                        else // standing, no control
                        {
                            LadderAnim(character, LadderAnimation.IDLE);
                        }
                        
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
        
        private void ExitLadder(bool setFreeFallAnimation = true)
        {
            if(usingLadder == null)
                return;
            
            usingLadder = null;
            
            if(setFreeFallAnimation && character != null && lastLadderAnim != LadderAnimation.NONE)
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
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TerminalBlock), "LargeShipUsableLadder", "SmallShipUsableLadder")]
    public class LadderBlock : MyGameLogicComponent
    {
        public static readonly HashSet<string> ladderIds = new HashSet<string>()
        {
            "LargeShipUsableLadder",
            "SmallShipUsableLadder"
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
            
            block.SetValueBool("ShowInTerminal", false);
            block.SetValueBool("ShowInToolbarConfig", false);
            
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
            var block = Entity as Ingame.IMyGravityGeneratorBase;
            
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
