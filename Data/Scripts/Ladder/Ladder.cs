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
        public class PlayerOnLadder
        {
            public IMyEntity character = null;
            public MyCharacterDefinition characterDefinition = null;
            public LadderAction action = LadderAction.ON_LADDER;
            public IMyCubeBlock ladder = null;
            
            public float climb = 0;
            public float side = 0;
            public bool sprint = false;
            public float progress = 2;
            public float travel = 0;
            public int timeout = 0; // TODO implement a check to avoid infinite climbing for people that lost internet connection? TODO actually test if it's necessary
            
            public void StepSound(int targetTick)
            {
                if(!MyAPIGateway.Multiplayer.IsServer)
                    return;
                
                if(travel >= targetTick)
                {
                    travel = 0;
                    var position = character.WorldMatrix.Translation;
                    var bytes = encode.GetBytes(character.EntityId.ToString());
                    MyAPIGateway.Players.GetPlayers(LadderMod.players, delegate(IMyPlayer p)
                                                    {
                                                        if(Vector3D.DistanceSquared(p.GetPosition(), position) <= STEP_RANGE_SQ)
                                                        {
                                                            MyAPIGateway.Multiplayer.SendMessageTo(PACKET_STEP, bytes, p.SteamUserId, false);
                                                        }
                                                        
                                                        return false;
                                                    });
                }
            }
        }
        
        public enum LadderAction
        {
            MOUNT,
            DISMOUNT,
            LET_GO,
            JUMP_OFF,
            ON_LADDER,
            CLIMB,
            CHANGE_LADDER,
        }
        
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
        
        private bool init = false;
        private bool isDedicated = false;
        
        private IMyEntity character = null;
        private MyCharacterDefinition characterDefinition = null;
        private IMyTerminalBlock usingLadder = null;
        private float mounting = 2f;
        private float dismounting = 2;
        private MyOrientedBoundingBoxD ladderBox = new MyOrientedBoundingBoxD();
        private Vector3 gravity = Vector3.Zero;
        private short skipPlanets = 0;
        private byte skipRetryGravity = GRAVITY_UPDATERATE;
        private byte skipRefreshAnim = 0;
        private bool alignedToGravity = false;
        private bool grabOnLoad = false;
        
        private MyCubeBlockDefinition prevCubeBuilderDefinition = null;
        
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
        
        public static readonly MySoundPair soundStep = new MySoundPair("PlayerLadderStep");
        
        public static Dictionary<long, MyPlanet> planets = new Dictionary<long, MyPlanet>();
        public static Dictionary<long, IMyGravityGeneratorBase> gravityGenerators = new Dictionary<long, IMyGravityGeneratorBase>();
        
        public static IMyHudNotification status;
        public static Dictionary<long, IMyTerminalBlock> ladders = new Dictionary<long, IMyTerminalBlock>();
        
        private PlayerOnLadder myLadderStatus = new PlayerOnLadder();
        private Dictionary<ulong, PlayerOnLadder> playersOnLadder = null;
        private List<ulong> removePlayersOnLadder = new List<ulong>();
        
        private StringBuilder packetData = new StringBuilder();
        private HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
        public static List<IMyPlayer> players = new List<IMyPlayer>();
        
        public const float ALIGN_STEP = 0.01f;
        public const float ALIGN_MUL = 1.2f;
        public const float TICKRATE = 1f/60f;
        public const float UPDATE_RADIUS = 10f;
        public const float EXTRA_OFFSET_Z = 0.5f;
        public const double RAY_HEIGHT = 1.7;
        
        public const byte GRAVITY_UPDATERATE = 15;
        
        public const ushort PACKET_STEP = 23991;
        public const ushort PACKET_LADDERDATA = 23992;
        public const char SEPARATOR = ' ';
        public const int STEP_RANGE_SQ = 100*100;
        
        public static readonly Encoding encode = Encoding.Unicode;
        
        public const string LEARN_UNCHECK = "[  ] ";
        public const string LEARN_CHECK = "[x] ";
        public static readonly MyStringId CUSTOMTEXT = MyStringId.GetOrCompute("{0}");
        
        private LadderAnimation lastLadderAnim = LadderAnimation.NONE;
        
        public const float VEL_CLIMB = 120;
        public const float VEL_SPRINT = 200;
        public const float VEL_SIDE = 100;
        public const float VEL_JUMP = 250;
        public const float CHAR_SPEED_MUL = 30;
        
        public const float ALIGN_ACCURACY = 0.005f;
        
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
            
            if(MyAPIGateway.Multiplayer.IsServer)
            {
                playersOnLadder = new Dictionary<ulong, PlayerOnLadder>();
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET_LADDERDATA, ReceivedLadderDataPacket);
            }
            
            if(!isDedicated)
            {
                settings = new Settings();
                
                string worldVariables;
                
                if(MyAPIGateway.Utilities.GetVariable("LadderMod", out worldVariables))
                {
                    grabOnLoad = worldVariables.Contains("grabonload");
                }
                
                if(MyAPIGateway.Multiplayer.IsServer)
                    settings.clientPrediction = false; // lazy fix for the sounds not playing for hosts xD
                
                MyAPIGateway.Utilities.MessageEntered += MessageEntered;
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET_STEP, ReceivedStepPacket);
                
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
                    if(usingLadder != null)
                    {
                        MyAPIGateway.Utilities.SetVariable("LadderMod", "grabonload");
                    }
                    else
                    {
                        MyAPIUtilities.Static.Variables.Remove("LadderMod");
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
                if(init)
                {
                    init = false;
                    ladders.Clear();
                    planets.Clear();
                    gravityGenerators.Clear();
                    
                    if(MyAPIGateway.Multiplayer.IsServer)
                    {
                        MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET_LADDERDATA, ReceivedLadderDataPacket);
                        
                        if(playersOnLadder != null)
                        {
                            playersOnLadder.Clear();
                            playersOnLadder = null;
                        }
                    }
                    
                    if(!isDedicated)
                    {
                        MyAPIGateway.Utilities.MessageEntered -= MessageEntered;
                        MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET_STEP, ReceivedStepPacket);
                        
                        if(settings != null)
                        {
                            settings.Close();
                            settings = null;
                        }
                        
                        SaveLearn();
                    }
                    
                    Log.Info("Mod unloaded.");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            Log.Close();
        }
        
        public void ReceivedStepPacket(byte[] bytes)
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
        
        public void ReceivedLadderDataPacket(byte[] bytes)
        {
            try
            {
                string[] data = encode.GetString(bytes).Split(SEPARATOR);
                int i = 0;
                LadderAction action = (LadderAction)int.Parse(data[i++]);
                long charId = long.Parse(data[i++]);
                
                if(!MyAPIGateway.Entities.EntityExists(charId))
                {
                    Log.Error("Ladder packet error: character entity not found! id="+charId);
                    return;
                }
                
                ulong steamId = ulong.Parse(data[i++]);
                var charEnt = MyAPIGateway.Entities.GetEntityById(charId);
                
                PlayerOnLadder ld = null;
                playersOnLadder.TryGetValue(steamId, out ld);
                
                if(ld == null)
                {
                    if(action == LadderAction.MOUNT)
                    {
                        ld = new PlayerOnLadder();
                        playersOnLadder.Add(steamId, ld);
                    }
                    else
                    {
                        return; // ignore any action if not mounted
                    }
                }
                
                if(ld.character != charEnt)
                {
                    ld.character = charEnt;
                    ld.characterDefinition = GetCharacterDefinitionFrom(charEnt);
                    
                    if(ld.characterDefinition == null)
                        Log.Error("Couldn't get character definition for entity: "+charEnt);
                }
                
                switch(action)
                {
                    case LadderAction.MOUNT:
                    case LadderAction.DISMOUNT:
                        {
                            ld.action = action;
                            ld.progress = ALIGN_STEP;
                            ld.travel = 0;
                            
                            if(action == LadderAction.MOUNT)
                            {
                                long ladderId = long.Parse(data[i++]);
                                ld.ladder = (MyAPIGateway.Entities.EntityExists(ladderId) ? MyAPIGateway.Entities.GetEntityById(ladderId) as IMyCubeBlock : null);
                                
                                if(ld.ladder == null)
                                    playersOnLadder.Remove(steamId);
                            }
                            
                            return;
                        }
                    case LadderAction.LET_GO:
                        {
                            ld.StepSound(0);
                            
                            playersOnLadder.Remove(steamId);
                            return;
                        }
                    case LadderAction.JUMP_OFF:
                        {
                            var jumpDir = new Vector3D(double.Parse(data[i++]), double.Parse(data[i++]), double.Parse(data[i++]));
                            
                            ld.character.Physics.LinearVelocity += jumpDir * (ld.characterDefinition == null ? VEL_JUMP : ld.characterDefinition.Mass * ld.characterDefinition.JumpForce) * TICKRATE;
                            ld.StepSound(0);
                            
                            playersOnLadder.Remove(steamId);
                            return;
                        }
                    case LadderAction.CLIMB:
                        {
                            // don't set the action, it'll run in ON_LADDER
                            ld.climb = float.Parse(data[i++]);
                            ld.side = float.Parse(data[i++]);
                            ld.sprint = int.Parse(data[i++]) == 1;
                            return;
                        }
                    case LadderAction.CHANGE_LADDER:
                        {
                            long ladderId = long.Parse(data[i++]);
                            ld.ladder = (MyAPIGateway.Entities.EntityExists(ladderId) ? MyAPIGateway.Entities.GetEntityById(ladderId) as IMyCubeBlock : null);
                            
                            if(ld.ladder == null)
                            {
                                playersOnLadder.Remove(steamId);
                                return;
                            }
                            
                            return;
                        }
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
        
        private void SetCharacterReference(IMyEntity ent)
        {
            if(character == ent)
                return;
            
            character = ent;
            characterDefinition = GetCharacterDefinitionFrom(ent);
        }
        
        private MyCharacterDefinition GetCharacterDefinitionFrom(IMyEntity ent)
        {
            if(ent != null)
            {
                var obj = ent.GetObjectBuilder(false) as MyObjectBuilder_Character;
                
                if(obj != null)
                {
                    MyCharacterDefinition def;
                    MyDefinitionManager.Static.Characters.TryGetValue(obj.CharacterModel, out def);
                    return def;
                }
            }
            
            return null;
        }
        
        public override void UpdateBeforeSimulation()
        {
            try
            {
                if(!init)
                {
                    if(MyAPIGateway.Session == null)
                        return;
                    
                    Init();
                }
                
                if(MyAPIGateway.Multiplayer.IsServer && playersOnLadder != null && playersOnLadder.Count > 0)
                {
                    foreach(var kv in playersOnLadder) // server side loop for ladder movement
                    {
                        var ld = kv.Value;
                        
                        if(ld.character == null || ld.ladder == null || ld.character.Physics == null || ld.ladder.CubeGrid.Physics == null)
                        {
                            removePlayersOnLadder.Add(kv.Key);
                            continue;
                        }
                        
                        // always sync velocity with the ladder
                        ld.character.Physics.LinearVelocity = kv.Value.ladder.CubeGrid.Physics.GetVelocityAtPoint(ld.character.WorldMatrix.Translation);
                        
                        switch(kv.Value.action)
                        {
                            case LadderAction.ON_LADDER:
                                {
                                    var ladderMatrix = ld.ladder.WorldMatrix;
                                    
                                    if(ld.climb != 0) // climbing up/down
                                    {
                                        float speed = (ld.characterDefinition == null ? (ld.sprint ? VEL_SPRINT : VEL_CLIMB) : CHAR_SPEED_MUL * (ld.sprint ? ld.characterDefinition.MaxSprintSpeed : ld.characterDefinition.MaxRunSpeed));
                                        
                                        ld.character.Physics.LinearVelocity += ladderMatrix.Up * ld.climb * speed * TICKRATE;
                                        ld.travel += Math.Abs(ld.climb) * speed * TICKRATE;
                                    }
                                    
                                    float speedSide = (ld.characterDefinition == null ? (ld.sprint ? VEL_SIDE : VEL_CLIMB) : CHAR_SPEED_MUL * (ld.sprint ? ld.characterDefinition.MaxSprintSpeed : ld.characterDefinition.MaxRunStrafingSpeed));
                                    
                                    if(ld.side != 0) // moving sideways
                                    {
                                        ld.character.Physics.LinearVelocity += ladderMatrix.Left * ld.side * speedSide * TICKRATE;
                                        ld.travel += Math.Abs(ld.side) * speedSide * TICKRATE;
                                    }
                                    else // move player back on to the ladder sideways
                                    {
                                        var ladderInternal = ld.ladder as MyCubeBlock;
                                        var charOnLadder = ladderMatrix.Translation + ladderMatrix.Forward * (ladderInternal.BlockDefinition.ModelOffset.Z + EXTRA_OFFSET_Z);
                                        
                                        if(ld.ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                                            charOnLadder += ladderMatrix.Backward;
                                        
                                        var charPos = ld.character.WorldMatrix.Translation + ld.character.WorldMatrix.Up * 0.05;
                                        Vector3 dir = charOnLadder - charPos;
                                        Vector3 vel = dir - (ladderMatrix.Up * Vector3D.Dot(dir, ladderMatrix.Up));
                                        float len = vel.Normalize();
                                        
                                        if(len >= ALIGN_ACCURACY)
                                        {
                                            len = MathHelper.Clamp(len, 0.1f, 1);
                                            ld.character.Physics.LinearVelocity += vel * len * speedSide * TICKRATE;
                                            ld.travel += len * speedSide * TICKRATE;
                                        }
                                    }
                                    
                                    ld.StepSound(60);
                                    break;
                                }
                            case LadderAction.MOUNT:
                                {
                                    ld.progress *= ALIGN_MUL;
                                    
                                    var ladderMatrix = ld.ladder.WorldMatrix;
                                    var ladderInternal = ld.ladder as MyCubeBlock;
                                    var charOnLadder = ladderMatrix.Translation + ladderMatrix.Forward * (ladderInternal.BlockDefinition.ModelOffset.Z + EXTRA_OFFSET_Z);
                                    
                                    if(ld.ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                                        charOnLadder += ladderMatrix.Backward;
                                    
                                    float align = Vector3.Dot(ladderMatrix.Up, ld.character.WorldMatrix.Up);
                                    
                                    var matrix = MatrixD.CreateFromDir(ladderMatrix.Backward, (align > 0 ? ladderMatrix.Up : ladderMatrix.Down));
                                    var halfY = ((ladderInternal.BlockDefinition.Size.Y * ld.ladder.CubeGrid.GridSize) / 2);
                                    var diff = Vector3D.Dot(ld.character.WorldMatrix.Translation, ladderMatrix.Up) - Vector3D.Dot(charOnLadder, ladderMatrix.Up);
                                    matrix.Translation = charOnLadder + ladderMatrix.Up * MathHelper.Clamp(diff, -halfY, halfY);
                                    
                                    // UNDONE DEBUG
                                    //{
                                    //    MyTransparentGeometry.AddPointBillboard("Square", Color.Red, charOnLadder, 0.1f, 0, 0, true);
                                    //    MyTransparentGeometry.AddPointBillboard("Square", Color.Yellow, ld.character.WorldMatrix.Translation, 0.1f, 0, 0, true);
                                    //    MyTransparentGeometry.AddPointBillboard("Square", Color.Green, matrix.Translation, 0.1f, 0, 0, true);
                                    //}
                                    
                                    ld.character.SetWorldMatrix(MatrixD.SlerpScale(ld.character.WorldMatrix, matrix, MathHelper.Clamp(ld.progress, 0.0f, 1.0f)));
                                    
                                    //if(mounting >= 0.75f && charCtrl.EnabledThrusts) // apparently not needed to be synchronized, left here in case it is in the future
                                    //    charCtrl.SwitchThrusts();
                                    
                                    ld.travel += 3f;
                                    ld.StepSound(30);
                                    
                                    if(ld.progress > 1)
                                        ld.action = LadderAction.ON_LADDER;
                                    
                                    break;
                                }
                            case LadderAction.DISMOUNT:
                                {
                                    ld.progress *= ALIGN_MUL;
                                    
                                    var ladderMatrix = ld.ladder.WorldMatrix;
                                    var ladderInternal = ld.ladder as MyCubeBlock;
                                    
                                    var charOnLadder = ladderMatrix.Translation + ladderMatrix.Forward * (ladderInternal.BlockDefinition.ModelOffset.Z + EXTRA_OFFSET_Z);
                                    
                                    if(ld.ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                                        charOnLadder += ladderMatrix.Backward;
                                    
                                    var matrix = ld.character.WorldMatrix;
                                    var topDir = Vector3D.Dot(matrix.Up, ladderMatrix.Up);
                                    var halfY = ((ladderInternal.BlockDefinition.Size.Y * ld.ladder.CubeGrid.GridSize) / 2);
                                    matrix.Translation = charOnLadder + (topDir > 0 ? ladderMatrix.Up : ladderMatrix.Down) * (halfY + 0.1f) + ladderMatrix.Backward * 0.75;
                                    
                                    ld.character.SetWorldMatrix(MatrixD.SlerpScale(ld.character.WorldMatrix, matrix, MathHelper.Clamp(ld.progress, 0.0f, 1.0f)));
                                    
                                    ld.travel += 3f;
                                    ld.StepSound(30);
                                    
                                    if(ld.progress > 1)
                                    {
                                        removePlayersOnLadder.Add(kv.Key);
                                        continue;
                                    }
                                    
                                    break;
                                }
                        }
                        
                        ld.StepSound(60);
                    }
                    
                    if(removePlayersOnLadder.Count > 0)
                    {
                        foreach(var k in removePlayersOnLadder)
                        {
                            playersOnLadder.Remove(k);
                        }
                        
                        removePlayersOnLadder.Clear();
                    }
                }
                
                if(isDedicated) // player's side from here on
                    return;
                
                var playerControlled = MyAPIGateway.Session.ControlledObject;
                
                if(playerControlled != null)
                {
                    if(playerControlled.Entity is IMyCharacter)
                    {
                        SetCharacterReference(playerControlled.Entity);
                    }
                    else if(playerControlled.Entity is Ingame.IMyCockpit) // in a seat, certainly not gonna climb ladders
                    {
                        SetCharacterReference(null);
                    }
                    // other cases depend on having the character controlled for a bit to get the reference
                    else if(character != null && character.Closed)
                    {
                        SetCharacterReference(null);
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
                    
                    var cb = MyAPIGateway.CubeBuilder as MyCubeBuilder;
                    
                    // Dynamically enable/disable UseModelIntersection on ladder blocks that you hold to have the useful effect
                    // of being able the block when another entity is blocking the grid space but not the blocks's physical shape.
                    // This will still have the side effect issue if you aim at a ladder block with the same ladder block.
                    if(cb.IsActivated && cb.HudBlockDefinition != null && LadderLogic.ladderIds.Contains(cb.HudBlockDefinition.Id.SubtypeName))
                    {
                        if(prevCubeBuilderDefinition == null || prevCubeBuilderDefinition.Id != cb.HudBlockDefinition.Id)
                        {
                            if(prevCubeBuilderDefinition != null)
                                prevCubeBuilderDefinition.UseModelIntersection = false;
                            
                            prevCubeBuilderDefinition = cb.HudBlockDefinition;
                            cb.HudBlockDefinition.UseModelIntersection = true;
                        }
                    }
                    else if(prevCubeBuilderDefinition != null)
                    {
                        prevCubeBuilderDefinition.UseModelIntersection = false;
                        prevCubeBuilderDefinition = null;
                    }
                    
                    var charCtrl = character as IMyControllableEntity;
                    bool controllingCharacter = (playerControlled != null && playerControlled.Entity is IMyCharacter);
                    IMyTerminalBlock ladder = null;
                    MyCubeBlock ladderInternal = null;
                    
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
                        
                        if(settings.clientPrediction)
                        {
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
                        }
                        
                        SetLadderStatus("Dismounting ladder...", MyFontEnum.White);
                        
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
                                
                                ladderBox.HalfExtent.Y = 0.25 + 0.06; // 6mm offset to avoid some inaccuracies
                                ladderBox.HalfExtent.Z = 0.5;
                                
                                if(l.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                                    ladderBox.Center += ladderMatrix.Backward;
                            }
                            else
                            {
                                ladderBox.HalfExtent = (ladderInternal.BlockDefinition.Size * l.CubeGrid.GridSize) / 2;
                                ladderBox.HalfExtent.Y += 0.06; // 6mm offset to avoid some inaccuracies
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
                        
                        bool readInput = InputHandler.IsInputReadable();
                        
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
                                    SetLadderStatus("Ladder interaction is unassigned, edit the settings.cfg file!\nFor now you can use the USE key.", MyFontEnum.Red);
                                    
                                    if(!MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.USE))
                                        return;
                                }
                                else
                                {
                                    bool use1 = (readInput && settings.useLadder1 != null && settings.useLadder1.AllPressed());
                                    bool use2 = (readInput && settings.useLadder2 != null && settings.useLadder2.AllPressed());
                                    
                                    if(!use1 && !use2)
                                    {
                                        StringBuilder assigned = new StringBuilder();
                                        
                                        if(settings.useLadder1 != null)
                                            assigned.Append(settings.useLadder1.GetFriendlyString().ToUpper());
                                        
                                        if(settings.useLadder2 != null)
                                        {
                                            string secondary = settings.useLadder2.GetFriendlyString();
                                            
                                            if(secondary.Length > 0)
                                            {
                                                if(assigned.Length > 0)
                                                    assigned.Append(" or ");
                                                
                                                assigned.Append(secondary.ToUpper());
                                            }
                                        }
                                        
                                        SetLadderStatus("Press "+assigned+" to use the ladder.", MyFontEnum.White);
                                        return;
                                    }
                                }
                            }
                            
                            skipRefreshAnim = 60;
                            mounting = (controllingCharacter ? ALIGN_STEP : 2);
                            usingLadder = ladder;
                            SendLadderData(LadderAction.MOUNT, entId: ladder.EntityId);
                            LadderAnim(character, LadderAnimation.MOUNTING);
                        }
                        
                        if(usingLadder != ladder)
                        {
                            usingLadder = ladder;
                            SendLadderData(LadderAction.CHANGE_LADDER, entId: ladder.EntityId);
                        }
                        
                        if(charCtrl.Entity.Physics == null)
                        {
                            ExitLadder(false);
                            return;
                        }
                        
                        if(settings.clientPrediction)
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
                            
                            if(settings.clientPrediction)
                            {
                                float align = Vector3.Dot(ladderMatrix.Up, character.WorldMatrix.Up);
                                
                                var matrix = MatrixD.CreateFromDir(ladderMatrix.Backward, (align > 0 ? ladderMatrix.Up : ladderMatrix.Down));
                                
                                var vC = character.WorldMatrix.Translation;
                                vC = vC - (ladderMatrix.Forward * Vector3D.Dot(vC, ladderMatrix.Forward)) - (ladderMatrix.Left * Vector3D.Dot(vC, ladderMatrix.Left));
                                var vL = charOnLadder - (ladderMatrix.Up * Vector3D.Dot(charOnLadder, ladderMatrix.Up));
                                matrix.Translation = vL + vC;
                                
                                character.SetWorldMatrix(MatrixD.SlerpScale(character.WorldMatrix, matrix, MathHelper.Clamp(mounting, 0.0f, 1.0f)));
                            }
                            
                            if(mounting >= 0.75f && charCtrl.EnabledThrusts) // delayed turning off thrusts because gravity aligns you faster and can make you fail to attach to the ladder
                                charCtrl.SwitchThrusts();
                            
                            SetLadderStatus("Mounting ladder...", MyFontEnum.White);
                            return;
                        }
                        
                        // TODO jetpack assited climb/descend ? / gravity assisted descend ?
                        
                        if(charCtrl.EnabledThrusts)
                        {
                            if(!learned[4])
                                learned[4] = true;
                            
                            ExitLadder(false); // leave ladder if jetpack is turned on
                            return;
                        }
                        
                        if(!controllingCharacter) // disable ladder control if not controlling character
                            readInput = false;
                        
                        bool movingSideways = false;
                        var analogInput = (readInput ? MyAPIGateway.Input.GetPositionDelta() : Vector3.Zero);
                        
                        if(analogInput.Y < 0) // crouch
                        {
                            if(!learned[4])
                                learned[4] = true;
                            
                            ExitLadder(false);
                            return;
                        }
                        
                        float move = MathHelper.Clamp((float)Math.Round(-analogInput.Z, 1), -1, 1); // forward/backward
                        float side = MathHelper.Clamp((float)Math.Round(analogInput.X, 1), -1, 1); // left/right
                        
                        //float move = readInput ? MathHelper.Clamp(MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.FORWARD) - MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.BACKWARD), -1, 1) : 0;
                        //float side = readInput ? MathHelper.Clamp(MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.STRAFE_RIGHT) - MyAPIGateway.Input.GetGameControlAnalogState(MyControlsSpace.STRAFE_LEFT), -1, 1) : 0;
                        var alignVertical = ladderMatrix.Up.Dot(character.WorldMatrix.Up);
                        
                        if(!loadedAllLearned)
                        {
                            bool allLearned = (learned[0] && learned[1] && learned[2] && learned[3] && learned[4]);
                            
                            for(int i = 0; i < learned.Length; i++)
                            {
                                if(learnNotify[i] == null)
                                    learnNotify[i] = new MyHudNotification(CUSTOMTEXT, 100, MyFontEnum.White, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, 5, MyNotificationLevel.Important);
                                
                                learnNotify[i].Text = (learned[i] ? LEARN_CHECK : LEARN_UNCHECK) + learnText[i];
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
                        
                        var view = MyAPIGateway.Session.ControlledObject.GetHeadMatrix(false, true);
                        float lookVertical = Vector3.Dot(character.WorldMatrix.Up, view.Forward);
                        float verticalModifier = MathHelper.Clamp((lookVertical + 0.65f) / 0.5f, -0.5f, 1.0f);
                        
                        if(verticalModifier < 0)
                            verticalModifier *= 2;
                        
                        move = (float)Math.Round(move * verticalModifier, 1);
                        
                        if(analogInput.Y > 0) // jump
                        {
                            if(MyHud.CharacterInfo.State == MyHudCharacterStateEnum.Standing) // this is still fine for avoiding jump as the character still is able to jump without needing feet on the ground
                            {
                                ExitLadder(false); // only release if on the floor as the character will jump regardless
                                return;
                            }
                            
                            if(settings.clientPrediction)
                                character.Physics.LinearVelocity += view.Forward * (characterDefinition == null ? VEL_JUMP : 200 * characterDefinition.JumpForce) * TICKRATE;
                            
                            SendLadderData(LadderAction.JUMP_OFF, vec: view.Forward);
                            LadderAnim(character, LadderAnimation.JUMP_OFF);
                            
                            if(!learned[3])
                                learned[3] = true;
                            
                            ExitLadder(false);
                            return;
                        }
                        
                        bool sprint = (characterDefinition != null && characterDefinition.Jetpack != null && MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.SPRINT));
                        
                        if(side != 0)
                        {
                            var alignForward = ladderMatrix.Backward.Dot(character.WorldMatrix.Forward);
                            
                            if(alignForward < 0)
                                side = -side;
                            
                            float speed = (characterDefinition == null ? (sprint ? VEL_SIDE : VEL_CLIMB) : CHAR_SPEED_MUL * (sprint ? characterDefinition.MaxSprintSpeed : characterDefinition.MaxRunStrafingSpeed));

                            if(settings.clientPrediction)
                                character.Physics.LinearVelocity += side * (alignVertical > 0 ? ladderMatrix.Left : ladderMatrix.Right) * speed * TICKRATE;
                            
                            LadderAnim(character, (side > 0 ? LadderAnimation.DISMOUNT_LEFT : LadderAnimation.DISMOUNT_RIGHT));
                            movingSideways = true;
                            
                            if(!learned[2])
                                learned[2] = true;
                        }
                        else
                        {
                            // aligning player to ladder
                            
                            if(settings.clientPrediction)
                            {
                                Vector3 dir = charOnLadder - charPos;
                                Vector3 vel = dir - (ladderMatrix.Up * Vector3D.Dot(dir, ladderMatrix.Up)); // projecting up/down direction to ignore it
                                float len = vel.Normalize();
                                
                                if(len >= ALIGN_ACCURACY)
                                {
                                    float speed = (characterDefinition == null ? (sprint ? VEL_SIDE : VEL_CLIMB) : CHAR_SPEED_MUL * (sprint ? characterDefinition.MaxRunStrafingSpeed : characterDefinition.MaxWalkStrafingSpeed));
                                    len = MathHelper.Clamp(len, 0.1f, 1);
                                    character.Physics.LinearVelocity += vel * len * speed * TICKRATE;
                                }
                            }
                        }
                        
                        if(move != 0)
                        {
                            if(!learned[0])
                                learned[0] = true;
                            
                            var halfY = ((ladderInternal.BlockDefinition.Size.Y * ladder.CubeGrid.GridSize) / 2);
                            var edge = charOnLadder + ((alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * halfY);
                            
                            // climb over at the end when climbing up
                            if(move > 0 && Vector3D.DistanceSquared(charPos, edge) <= 0.0625) // 0.25 squared
                            {
                                var nextBlockWorldPos = ladderMatrix.Translation + ladderMatrix.Forward * offset.Z + ((alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * (halfY + 0.1f));
                                var nextBlockPos = ladder.CubeGrid.WorldToGridInteger(nextBlockWorldPos);
                                var slim = ladder.CubeGrid.GetCubeBlock(nextBlockPos);
                                
                                // if the next block is not a ladder, dismount
                                if(slim == null || !(slim.FatBlock is IMyTerminalBlock) || !LadderBlock.ladderIds.Contains(slim.FatBlock.BlockDefinition.SubtypeId))
                                {
                                    dismounting = ALIGN_STEP;
                                    SendLadderData(LadderAction.DISMOUNT);
                                    return;
                                }
                            }
                            
                            // on the floor and moving backwards makes you dismount
                            if(move < 0 && MyHud.CharacterInfo.State == MyHudCharacterStateEnum.Standing)
                            {
                                // need to check the block under the ladder if it's anything but a ladder because "standing" stance occurs when character rests on its chest-sphere collision mesh too
                                var prevBlockWorldPos = ladderMatrix.Translation + ladderMatrix.Forward * offset.Z + (alignVertical > 0 ? ladderMatrix.Down : ladderMatrix.Up) * (halfY + 0.1f);
                                var prevBlockPos = ladder.CubeGrid.WorldToGridInteger(prevBlockWorldPos);
                                var slim = ladder.CubeGrid.GetCubeBlock(prevBlockPos);
                                
                                // if it's not a ladder, check the distance and confirm your feet are close to its edge
                                if(slim == null || !(slim.FatBlock is IMyTerminalBlock) || !LadderBlock.ladderIds.Contains(slim.FatBlock.BlockDefinition.SubtypeId))
                                {
                                    // get the block's edge and the character feet position only along the ladder's up/down axis
                                    var blockPosProjectedUp = ladderMatrix.Up * Vector3D.Dot(prevBlockWorldPos, ladderMatrix.Up);
                                    var charPosProjectedUp = ladderMatrix.Up * Vector3D.Dot(character.WorldMatrix.Translation, ladderMatrix.Up);
                                    
                                    if(Vector3D.DistanceSquared(blockPosProjectedUp, charPosProjectedUp) <= 0.04) // 0.2 squared
                                    {
                                        ExitLadder(false); // to recap: if you're moving char-relative down and in "standing" stance and the block below is not a ladder and you're closer than 0.1m to its edge, then let go of the ladder.
                                        return;
                                    }
                                }
                            }
                            
                            // climbing on the ladder
                            if(move != 0)
                            {
                                if(!learned[1] && sprint)
                                    learned[1] = true;
                                
                                float speed = (characterDefinition == null ? (sprint ? VEL_SPRINT : VEL_CLIMB) : CHAR_SPEED_MUL * (sprint ? characterDefinition.MaxSprintSpeed : characterDefinition.MaxRunSpeed));
                                
                                if(settings.clientPrediction)
                                    character.Physics.LinearVelocity += (alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * move * speed * TICKRATE;
                                
                                if(!movingSideways)
                                    LadderAnim(character, (move > 0 ? LadderAnimation.UP : LadderAnimation.DOWN));
                            }
                        }
                        else if(!movingSideways)
                        {
                            LadderAnim(character, LadderAnimation.IDLE);
                        }
                        
                        SendLadderData(LadderAction.CLIMB,
                                       climb: (alignVertical > 0 ? move : -move),
                                       side: (alignVertical > 0 ? side : -side),
                                       sprint: sprint);
                        
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
        
        private void SendLadderData(LadderAction action, Vector3D? vec = null, long? entId = null, float? climb = null, float? side = null, bool? sprint = null)
        {
            //if(MyAPIGateway.Multiplayer.IsServer)
            //    return;
            
            switch(action)
            {
                case LadderAction.CLIMB:
                    {
                        if(climb.Value == myLadderStatus.climb && side.Value == myLadderStatus.side && sprint.Value == myLadderStatus.sprint)
                            return;
                        
                        myLadderStatus.climb = climb.Value;
                        myLadderStatus.side = side.Value;
                        myLadderStatus.sprint = sprint.Value;
                        break;
                    }
                case LadderAction.DISMOUNT:
                case LadderAction.JUMP_OFF:
                case LadderAction.LET_GO:
                    {
                        myLadderStatus.side = 0;
                        myLadderStatus.climb = 0;
                        myLadderStatus.sprint = false;
                        break;
                    }
            }
            
            packetData.Clear();
            packetData.Append((int)action);
            packetData.Append(SEPARATOR);
            packetData.Append(character.EntityId);
            packetData.Append(SEPARATOR);
            packetData.Append(MyAPIGateway.Multiplayer.MyId);
            
            if(vec.HasValue)
            {
                packetData.Append(SEPARATOR);
                packetData.Append(vec.Value.X);
                packetData.Append(SEPARATOR);
                packetData.Append(vec.Value.Y);
                packetData.Append(SEPARATOR);
                packetData.Append(vec.Value.Z);
            }
            
            if(climb.HasValue && side.HasValue && sprint.HasValue)
            {
                packetData.Append(SEPARATOR);
                packetData.Append(climb.Value);
                packetData.Append(SEPARATOR);
                packetData.Append(side.Value);
                packetData.Append(SEPARATOR);
                packetData.Append(sprint.Value ? 1 : 0);
            }
            
            if(entId.HasValue)
            {
                packetData.Append(SEPARATOR);
                packetData.Append(entId.Value);
            }
            
            var bytes = encode.GetBytes(packetData.ToString());
            
            MyAPIGateway.Multiplayer.SendMessageToServer(PACKET_LADDERDATA, bytes, true);
        }
        
        private void ExitLadder(bool setFreeFallAnimation = true)
        {
            if(grabOnLoad)
                grabOnLoad = false;
            
            if(usingLadder == null)
                return;
            
            SendLadderData(LadderAction.LET_GO);
            
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
            
            if(skinned.UseNewAnimationSystem)
            {
                // TODO how does this even...
                /*
                var character = ent as IMyCharacter;
                character.TriggerCharacterAnimationEvent("SMBody_WalkRun".ToLower(), true);
                character.TriggerCharacterAnimationEvent("Sprint".ToLower(), true);
                 */
            }
            else
            {
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
