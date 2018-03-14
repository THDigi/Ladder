using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using IMyControllableEntity = Sandbox.Game.Entities.IMyControllableEntity;

// TODO better detection on the side that needs to be mounted on
// TODO better detection on which ladder to mount when multiple are close (smallship)
// TODO fix dismounting in air with magnetic boots
// TODO make it hook the astronaut from the waist onto a rail (http://www.aikencolon.com/assets/images/miller/15729/miller-honeywell-15729-glideloc-component-aluminum-vertical-rail.jpg)

namespace Digi.Ladder
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class LadderMod : MySessionComponentBase
    {
        public override void LoadData()
        {
            Log.SetUp("Ladder", 600062079);
        }

        public static LadderMod instance = null;

        private bool init = false;
        private bool isServer = false;
        private bool isDedicated = false;
        private Settings settings = null;

        private IMyCharacter character = null;
        private MyCharacterDefinition characterDefinition = null;
        private IMyTerminalBlock usingLadder = null;
        private IMyTerminalBlock foundLadder = null;
        private float mounting = 2f;
        private float dismounting = 2;
        private MyOrientedBoundingBoxD ladderBox = new MyOrientedBoundingBoxD();
        private byte skipRetryGravity = GRAVITY_UPDATERATE;
        private byte skipRefreshAnim = 0;
        private bool alignedToGravity = false;
        private bool grabOnLoad = false;
        private bool aimingAtLadder = false;
        private IMyTerminalBlock highlightedLadderCenter = null;
        private List<string> highlightedLadders = new List<string>();

        private MyCubeBlockDefinition prevCubeBuilderDefinition = null;

        //private MyEntity debugBox = null; // UNDONE DEBUG

        private const string LEARNFILE = "learned";
        private bool loadedAllLearned = false;
        private readonly bool[] learned = new bool[5];
        private readonly IMyHudNotification[] learnNotify = new IMyHudNotification[5];
        private readonly string[] learnText =
        {
            "Move forward/backward to climb",
            "Sprint to climb/descend faster",
            "Strafe left/right to dismount left or right",
            "Jump to jump off in the direction you're looking",
            "Crouch or turn on jetpack to dismount"
        };

        public readonly HashSet<string> ladderIds = new HashSet<string>()
        {
            "LargeShipUsableLadder",
            "SmallShipUsableLadder",
            "SmallShipUsableLadderSegment",
            "LargeShipUsableLadderRetractable",
            "SmallShipUsableLadderRetractable"
        };

        private readonly MySoundPair soundStep = new MySoundPair("PlayStepsMetal");
        private readonly Dictionary<long, MyEntity3DSoundEmitter> soundEmitters = new Dictionary<long, MyEntity3DSoundEmitter>();
        private short skipCleanEmitters = 0;

        public static readonly Dictionary<long, IMyTerminalBlock> ladders = new Dictionary<long, IMyTerminalBlock>();

        private IMyHudNotification status = null;
        private readonly PlayerOnLadder myLadderStatus = new PlayerOnLadder();
        private Dictionary<ulong, PlayerOnLadder> playersOnLadder = null;
        private readonly List<ulong> removePlayersOnLadder = new List<ulong>();

        private readonly StringBuilder tmp = new StringBuilder();
        private readonly HashSet<IMyEntity> ents = new HashSet<IMyEntity>();

        public const float ALIGN_STEP = 0.01f;
        public const float ALIGN_MUL = 1.2f;
        public const float TICKRATE = 1f / 60f;
        public const float UPDATE_RADIUS = 10f;
        public const float EXTRA_OFFSET_Z = 0.5f;
        public const double RAY_HEIGHT = 1.7;

        public const byte GRAVITY_UPDATERATE = 15;

        public const ushort PACKET_STEP = 23991;
        public const ushort PACKET_LADDERDATA = 23992;
        public const char SEPARATOR = ' ';
        public const int STEP_RANGE_SQ = 100 * 100;

        public readonly Encoding encode = Encoding.Unicode;

        public const int LADDER_HIGHLIGHT_PULSE = 300;

        public const string LADDER_NAME_PREFIX = "Ladder-";

        public const string LEARN_UNCHECK = "[  ] ";
        public const string LEARN_CHECK = "[x] ";
        public readonly MyStringId CUSTOMTEXT = MyStringId.GetOrCompute("{0}");

        private LadderAnimation lastLadderAnim = LadderAnimation.NONE;

        public const float VEL_CLIMB = 120;
        public const float VEL_SPRINT = 200;
        public const float VEL_SIDE = 100;
        public const float VEL_JUMP = 250;
        public const float CHAR_SPEED_MUL = 30;

        public const float ALIGN_ACCURACY = 0.005f;

        private const int COLLISSIONLAYER_NOCHARACTER = 30;

        private readonly string[] ladderAnimations = new string[]
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
            instance = this;
            init = true;
            isServer = MyAPIGateway.Multiplayer.IsServer;
            isDedicated = isServer && MyAPIGateway.Utilities.IsDedicated;

            Log.Init();

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

                            if(line.StartsWith("//", StringComparison.Ordinal))
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
                tmp.Clear();

                if(learned[0])
                    tmp.AppendLine("move_climb");

                if(learned[1])
                    tmp.AppendLine("move_sprint");

                if(learned[2])
                    tmp.AppendLine("side_dismount");

                if(learned[3])
                    tmp.AppendLine("jump_dismount");

                if(learned[4])
                    tmp.AppendLine("crouch_dismount");

                if(tmp.Length > 0)
                {
                    var file = MyAPIGateway.Utilities.WriteFileInLocalStorage(LEARNFILE, typeof(LadderMod));
                    file.Write(tmp.ToString());
                    file.Flush();
                    file.Close();
                }

                tmp.Clear();
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
                instance = null;
                ladders.Clear();

                if(init)
                {
                    init = false;

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
                long entId = BitConverter.ToInt64(bytes, 0);

                if(MyAPIGateway.Entities.EntityExists(entId))
                {
                    var ent = MyAPIGateway.Entities.GetEntityById(entId);
                    MyEntity3DSoundEmitter emitter;

                    if(!soundEmitters.TryGetValue(entId, out emitter))
                    {
                        emitter = new MyEntity3DSoundEmitter(ent as MyEntity);
                        soundEmitters.Add(entId, emitter);
                    }

                    emitter.PlaySound(soundStep);
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
                int index = 0;

                var action = (LadderAction)bytes[index];
                index += sizeof(byte);

                long charId = BitConverter.ToInt64(bytes, index);
                index += sizeof(long);

                if(!MyAPIGateway.Entities.EntityExists(charId))
                {
                    Log.Error("Ladder packet error: character entity not found! id=" + charId);
                    return;
                }

                var charEnt = MyAPIGateway.Entities.GetEntityById(charId);

                ulong steamId = BitConverter.ToUInt64(bytes, index);
                index += sizeof(ulong);

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
                        Log.Error("Couldn't get character definition for entity: " + charEnt);
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
                                long ladderId = BitConverter.ToInt64(bytes, index);
                                index += sizeof(long);

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
                            var jumpDir = new Vector3(BitConverter.ToSingle(bytes, index),
                                                       BitConverter.ToSingle(bytes, index + sizeof(float)),
                                                       BitConverter.ToSingle(bytes, index + sizeof(float) * 2));
                            index += sizeof(float) * 3;

                            ld.character.Physics.LinearVelocity += jumpDir * (ld.characterDefinition == null ? VEL_JUMP : ld.characterDefinition.Mass * ld.characterDefinition.JumpForce) * TICKRATE;
                            ld.StepSound(0);

                            playersOnLadder.Remove(steamId);
                            return;
                        }
                    case LadderAction.CLIMB:
                        {
                            // don't set the action, it'll run in ON_LADDER

                            ld.climb = BitConverter.ToSingle(bytes, index);
                            index += sizeof(float);

                            ld.side = BitConverter.ToSingle(bytes, index);
                            index += sizeof(float);

                            ld.sprint = BitConverter.ToBoolean(bytes, index);
                            index += sizeof(bool);

                            return;
                        }
                    case LadderAction.CHANGE_LADDER:
                        {
                            long ladderId = BitConverter.ToInt64(bytes, index);
                            index += sizeof(long);

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

        public void SetLadderStatus(string text, string font, int aliveTime = 100)
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

        private void SetCharacterReference(IMyEntity ent)
        {
            if(character == ent || !(ent is IMyCharacter))
                return;

            character = ent as IMyCharacter;

            if(character == null)
                return;

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

                if(++skipCleanEmitters > 3600)
                {
                    List<long> removeKeys = null;

                    foreach(var entId in soundEmitters.Keys)
                    {
                        if(!MyAPIGateway.Entities.EntityExists(entId))
                        {
                            if(removeKeys == null)
                                removeKeys = new List<long>();

                            removeKeys.Add(entId);
                        }
                    }

                    if(removeKeys != null)
                    {
                        foreach(var key in removeKeys)
                        {
                            soundEmitters.Remove(key);
                        }
                    }
                }

                if(isServer && playersOnLadder != null && playersOnLadder.Count > 0)
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

                                    if(Math.Abs(ld.climb) > 0.0001f) // climbing up/down
                                    {
                                        float speed = (ld.characterDefinition == null ? (ld.sprint ? VEL_SPRINT : VEL_CLIMB) : CHAR_SPEED_MUL * (ld.sprint ? ld.characterDefinition.MaxSprintSpeed : ld.characterDefinition.MaxRunSpeed));

                                        ld.character.Physics.LinearVelocity += ladderMatrix.Up * ld.climb * speed * TICKRATE;
                                        ld.travel += Math.Abs(ld.climb) * speed * TICKRATE;
                                    }

                                    float speedSide = (ld.characterDefinition == null ? (ld.sprint ? VEL_SIDE : VEL_CLIMB) : CHAR_SPEED_MUL * (ld.sprint ? ld.characterDefinition.MaxSprintSpeed : ld.characterDefinition.MaxRunStrafingSpeed));

                                    if(Math.Abs(ld.side) > 0.0001f) // moving sideways
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

                                    var matrix = MatrixD.CreateFromDir(ladderMatrix.Backward, (align >= 0.1f ? ladderMatrix.Up : ladderMatrix.Down));
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

                if(!isDedicated)
                {
                    aimingAtLadder = false;

                    PlayerUpdate();

                    if(!aimingAtLadder && highlightedLadderCenter != null)
                    {
                        highlightedLadderCenter = null;

                        if(highlightedLadders.Count > 0)
                        {
                            foreach(var name in highlightedLadders)
                            {
                                MyVisualScriptLogicProvider.SetHighlight(name, false);
                            }

                            highlightedLadders.Clear();
                        }
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        // DEBUG experimental rotation limit
        // NOTE: removes the ability to jump off of the ladder directly backwards which is physically possible IRL, so this is not a good thing anyway
#if false
        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!init || isDedicated || usingLadder == null)
                    return;

                var ladderMatrix = usingLadder.WorldMatrix;
                var charMatrix = character.WorldMatrix;

                var dotHorizontal = ladderMatrix.Forward.Dot(charMatrix.Forward);
                var dotLeft = ladderMatrix.Left.Dot(charMatrix.Forward);
                var dotUp = ladderMatrix.Up.Dot(charMatrix.Up);

                //MyAPIGateway.Utilities.ShowNotification($"dotHorizontal={dotHorizontal:0.00}; dotLeft={dotLeft:0.00}; dotUp={dotUp:0.00}", 16); // DEBUG print

                const double LIMIT_ANGLE = 0.1;

                if(dotHorizontal > -LIMIT_ANGLE)
                {
                    // HACK random vector required to apply matrix a 2nd time without moving
                    var randVec = (ladderMatrix.Up * MyUtils.GetRandomDouble(-0.001, 0.001));
                    var angle = Math.Acos(LIMIT_ANGLE);

                    if((dotUp > 0 && dotLeft > 0) || (dotUp < 0 && dotLeft < 0))
                        angle = -angle;

                    var pos = charMatrix.Translation + randVec;
                    charMatrix = MatrixD.CreateWorld(Vector3D.Zero, ladderMatrix.Backward, charMatrix.Up);
                    charMatrix *= MatrixD.CreateFromAxisAngle(charMatrix.Up, angle);
                    charMatrix.Translation = pos;

                    character.WorldMatrix = charMatrix;

                    //MyAPIGateway.Utilities.ShowNotification("set the matrix", 16); // DEBUG print
                }

                // other experiments
                {
                        //if(controllingCharacter)
                        //{
                        //    var m = character.WorldMatrix;

                        //    var dotH = ladderMatrix.Forward.Dot(m.Forward);
                        //    var dotLR = ladderMatrix.Left.Dot(m.Forward);

                        //    MyAPIGateway.Utilities.ShowNotification($"dotH={dotH:0.00}; dotLR={dotLR:0.00}", 16); // DEBUG

                        //    const double LIMIT_ANGLE = 0.5;

                        //    if(dotH > -LIMIT_ANGLE)
                        //    {
                        //        // HACK random vector required to apply matrix a 2nd time without moving
                        //        var randVec = (ladderMatrix.Up * MyUtils.GetRandomDouble(-0.001, 0.001));

                        //        var pos = m.Translation + randVec;
                        //        m = MatrixD.CreateWorld(Vector3D.Zero, ladderMatrix.Backward, m.Up);
                        //        m *= MatrixD.CreateFromAxisAngle(m.Up, (dotLR > 0 ? -Math.Acos(LIMIT_ANGLE) : Math.Acos(LIMIT_ANGLE)));
                        //        m.Translation = pos;

                        //        character.WorldMatrix = m;

                        //        MyAPIGateway.Utilities.ShowNotification("set the matrix", 16); // DEBUG
                        //    }
                        //}

                        //if(camCtrl == null || controller == null)
                        //    return;

                        //if(controller is IMyShipController)
                        //{
                        //    // HACK this is how MyCockpit.Rotate() does things so I kinda have to use these magic numbers.
                        //    var num = MyAPIGateway.Input.GetMouseSensitivity() * 0.13f;
                        //    camCtrl.Rotate(new Vector2(controller.HeadLocalXAngle / num, controller.HeadLocalYAngle / num), 0);
                        //}
                        //else
                        //{
                        //    // HACK this is how MyCharacter.RotateHead() does things so I kinda have to use these magic numbers.
                        //    camCtrl.Rotate(new Vector2(controller.HeadLocalXAngle * 2, controller.HeadLocalYAngle * 2), 0);
                        //}

                        //character.SetLocalMatrix(Matrix.CreateFromYawPitchRoll(2, 2, -2));

                        //var m = character.WorldMatrix;
                        //
                        //var dotH = (float)ladderMatrix.Forward.Dot(m.Forward);
                        //var ang = (float)ladderMatrix.Forward.Dot(m.Left) * 90;
                        //MyAPIGateway.Utilities.ShowNotification("dotH=" + Math.Round(dotH, 2) + "; ang = " + Math.Round(ang, 2), 17); // DEBUG
                        //
                        //if(dotH > -1)
                        //{
                        //    m = MatrixD.CreateFromDir(ladderMatrix.Forward, m.Up);
                        //    m.Translation = character.WorldMatrix.Translation;
                        //    character.SetWorldMatrix(m);
                        //}

                        // only works for vertical
                        //charCtrl.HeadLocalYAngle = 0;
                        //charCtrl.HeadLocalXAngle = 0;

                        //MyAPIGateway.Session.CameraController.Rotate(new Vector2(-5f, -5f), 5f); // only works for vertical

                        //charCtrl.MoveAndRotate(Vector3.Zero, new Vector2(0, -5f), 5f); // only works for vertical

                        //character.Physics.AngularVelocity += Vector3.One * 10000000; // does nothing

                        //character.Physics.AddForce(MyPhysicsForceType.ADD_BODY_FORCE_AND_BODY_TORQUE, null, null, Vector3.One * 1000000000000); // doesn't quite work

                        //character.Physics.SetSpeeds(Vector3.Zero, Vector3.One * 10000000); // moves weirdly
                    }

}
        catch(Exception e)
        {
            Log.Error(e);
        }
    }
#endif

        private bool CheckLadder(IMyTerminalBlock l, RayD charRay, Vector3D charPos2)
        {
            if(Vector3D.DistanceSquared(l.WorldMatrix.Translation, charRay.Position) <= UPDATE_RADIUS)
            {
                var ladderInternal = l as MyCubeBlock;
                var ladderMatrix = l.WorldMatrix;

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

                if(!ladderBox.Contains(ref charRay.Position) && !ladderBox.Contains(ref charPos2))
                {
                    var intersect = ladderBox.Intersects(ref charRay);

                    if(!intersect.HasValue || intersect.Value < 0 || intersect.Value > RAY_HEIGHT)
                        return false;
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

                return true;
            }

            return false;
        }

        private void PlayerUpdate()
        {
            var playerControlled = MyAPIGateway.Session.ControlledObject;

            if(playerControlled != null)
            {
                if(playerControlled.Entity is IMyCharacter)
                {
                    SetCharacterReference(playerControlled.Entity);
                }
                else if(playerControlled.Entity is IMyCockpit) // in a seat, certainly not gonna climb ladders
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
                var cb = MyCubeBuilder.Static;

                // Dynamically enable/disable UseModelIntersection on ladder blocks that you hold to have the useful effect
                // of being able the block when another entity is blocking the grid space but not the blocks's physical shape.
                // This will still have the side effect issue if you aim at a ladder block with the same ladder block.
                if(cb.IsActivated && cb.CubeBuilderState != null && cb.CubeBuilderState.CurrentBlockDefinition != null && ladderIds.Contains(cb.CubeBuilderState.CurrentBlockDefinition.Id.SubtypeName))
                {
                    if(prevCubeBuilderDefinition == null || prevCubeBuilderDefinition.Id != cb.CubeBuilderState.CurrentBlockDefinition.Id)
                    {
                        if(prevCubeBuilderDefinition != null)
                            prevCubeBuilderDefinition.UseModelIntersection = false;

                        prevCubeBuilderDefinition = cb.CubeBuilderState.CurrentBlockDefinition;
                        cb.CubeBuilderState.CurrentBlockDefinition.UseModelIntersection = true;
                    }
                }
                else if(prevCubeBuilderDefinition != null)
                {
                    prevCubeBuilderDefinition.UseModelIntersection = false;
                    prevCubeBuilderDefinition = null;
                }

                var charCtrl = character as IMyControllableEntity;
                bool controllingCharacter = (playerControlled != null && playerControlled.Entity is IMyCharacter);
                var ladder = usingLadder ?? foundLadder;

                var charmatrix = character.WorldMatrix;
                var charPos = charmatrix.Translation + charmatrix.Up * 0.05;
                var charPos2 = charmatrix.Translation + charmatrix.Up * RAY_HEIGHT;
                var charRay = new RayD(charPos, charmatrix.Up);

                if(dismounting <= 1) // relative top dismount sequence
                {
                    if(usingLadder == null)
                    {
                        dismounting = 2;
                        return;
                    }

                    ladder = usingLadder;
                    var ladderInternal = (MyCubeBlock)ladder;
                    dismounting *= ALIGN_MUL;

                    if(settings.clientPrediction)
                    {
                        var ladderMatrix = ladder.WorldMatrix;
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

                    //SetLadderStatus("Dismounting ladder...", MyFontEnum.White);

                    if(dismounting > 1f)
                        ExitLadder(false);

                    return;
                }

                // UNDONE DEBUG
                //{
                //    var c = Color.Blue.ToVector4();
                //    MySimpleObjectDraw.DrawLine(charPos, charPos2, "WeaponLaserIgnoreDepth", ref c, 0.5f);
                //}

                #region Find a ladder
                // re-check last found ladder
                if(ladder != null && !CheckLadder(ladder, charRay, charPos2))
                    ladder = null;

                if(ladder == null)
                {
                    foreach(var l in ladders.Values)
                    {
                        if(l.Closed || l.MarkedForClose || !l.IsFunctional)
                            continue;

                        if(CheckLadder(l, charRay, charPos2))
                        {
                            ladder = l;
                            break;
                        }
                    }
                }
                #endregion Find a ladder

                #region Ladder client logic
                if(ladder != null)
                {
                    var ladderInternal = (MyCubeBlock)ladder;
                    var ladderMatrix = ladder.WorldMatrix;
                    var offset = ladderInternal.BlockDefinition.ModelOffset;
                    var charOnLadder = ladderMatrix.Translation + ladderMatrix.Forward * (offset.Z + EXTRA_OFFSET_Z);

                    if(ladder.CubeGrid.GridSizeEnum == MyCubeSize.Large)
                        charOnLadder += ladderMatrix.Backward;

                    if(++skipRetryGravity >= GRAVITY_UPDATERATE)
                    {
                        skipRetryGravity = 0;
                        alignedToGravity = true;
                        var gravity = MyParticlesManager.CalculateGravityInPoint(character.WorldMatrix.Translation);
                        var gravityLength = gravity.Normalize();

                        if(gravityLength > 0)
                        {
                            float gravDot = Vector3.Dot(gravity, ladderMatrix.Down);

                            if(!(gravDot >= 0.9f || gravDot <= -0.9f))
                            {
                                alignedToGravity = false;
                            }
                        }
                    }

                    bool readInput = InputHandler.IsInputReadable();

                    if(!alignedToGravity)
                    {
                        bool pressed = InputHandler.GetPressedOr(settings.useLadder1, settings.useLadder2, false, false) && InputHandler.IsInputReadable(); // needs to support hold-press, so don't set JustPressed to true!

                        if(pressed)
                            SetLadderStatus("Gravity not parallel to ladder!", MyFontEnum.Red, 1000);

                        if(usingLadder != null)
                            ExitLadder(false);

                        return;
                    }

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
                            var useObject = character.Components?.Get<MyCharacterDetectorComponent>()?.UseObject;

                            if(useObject != null)
                                return; // player aims at something else, don't interact with ladder

                            aimingAtLadder = true;

                            if(settings.useLadder1 == null && settings.useLadder2 == null)
                            {
                                SetLadderStatus("Ladder interaction is unassigned, edit the settings.cfg file!\nFor now you can use the USE key.", MyFontEnum.Red);

                                if(!MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.USE))
                                    return;
                            }
                            else
                            {
                                bool pressed = InputHandler.GetPressedOr(settings.useLadder1, settings.useLadder2, false, false) && InputHandler.IsInputReadable(); // needs to support hold-press, so don't set JustPressed to true!

                                if(!pressed)
                                {
                                    if(highlightedLadderCenter == null || highlightedLadderCenter.Closed || highlightedLadderCenter.EntityId != ladder.EntityId)
                                    {
                                        highlightedLadderCenter = ladder;

                                        if(highlightedLadders.Count > 0)
                                        {
                                            foreach(var name in highlightedLadders)
                                            {
                                                MyVisualScriptLogicProvider.SetHighlight(name, false);
                                            }

                                            highlightedLadders.Clear();
                                        }

                                        var envDef = MyDefinitionManager.Static.EnvironmentDefinition;
                                        var color = envDef.ContourHighlightColor;
                                        var thick = (int)envDef.ContourHighlightThickness;

                                        highlightedLadders.Add(ladder.Name);
                                        MyVisualScriptLogicProvider.SetHighlight(ladder.Name, true, thick, LADDER_HIGHLIGHT_PULSE, color);

                                        var ladderGrid = ladder.CubeGrid;
                                        const int scanBlocks = 3;

                                        for(int i = 1; i <= scanBlocks; i++)
                                        {
                                            var slim = ladderGrid.GetCubeBlock(ladderGrid.WorldToGridInteger(ladderMatrix.Translation + ladderMatrix.Up * i * ladderGrid.GridSize));

                                            if(slim?.FatBlock?.GameLogic?.GetAs<LadderBlock>() == null)
                                                break;

                                            if(!slim.FatBlock.IsFunctional)
                                                break;

                                            highlightedLadders.Add(slim.FatBlock.Name);

                                            float smoothStep = 1 - MathHelper.Clamp((i / (float)scanBlocks), 0f, 0.9f);
                                            var thickness = Math.Max((int)(thick * smoothStep), 1);
                                            MyVisualScriptLogicProvider.SetHighlight(slim.FatBlock.Name, true, thickness, LADDER_HIGHLIGHT_PULSE, (color * smoothStep));
                                        }

                                        for(int i = 1; i <= scanBlocks; i++)
                                        {
                                            var slim = ladderGrid.GetCubeBlock(ladderGrid.WorldToGridInteger(ladderMatrix.Translation + ladderMatrix.Down * i * ladderGrid.GridSize));

                                            if(slim?.FatBlock?.GameLogic?.GetAs<LadderBlock>() == null)
                                                break;

                                            if(!slim.FatBlock.IsFunctional)
                                                break;

                                            highlightedLadders.Add(slim.FatBlock.Name);

                                            float smoothStep = 1 - MathHelper.Clamp((i / (float)scanBlocks), 0f, 0.9f);
                                            var thickness = Math.Max((int)(thick * smoothStep), 1);
                                            MyVisualScriptLogicProvider.SetHighlight(slim.FatBlock.Name, true, thickness, LADDER_HIGHLIGHT_PULSE, (color * smoothStep));
                                        }
                                    }

                                    SetLadderStatus("Press " + InputHandler.GetFriendlyStringOr(settings.useLadder1, settings.useLadder2) + " to use the ladder.", MyFontEnum.White);
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

                            var matrix = MatrixD.CreateFromDir(ladderMatrix.Backward, (align >= 0.1f ? ladderMatrix.Up : ladderMatrix.Down));
                            var halfY = ((ladderInternal.BlockDefinition.Size.Y * ladder.CubeGrid.GridSize) / 2);
                            var diff = Vector3D.Dot(character.WorldMatrix.Translation, ladderMatrix.Up) - Vector3D.Dot(charOnLadder, ladderMatrix.Up);
                            matrix.Translation = charOnLadder + ladderMatrix.Up * MathHelper.Clamp(diff, -halfY, halfY);

                            character.SetWorldMatrix(MatrixD.SlerpScale(character.WorldMatrix, matrix, MathHelper.Clamp(mounting, 0.0f, 1.0f)));
                        }

                        if(mounting >= 0.75f && charCtrl.EnabledThrusts) // delayed turning off thrusts because gravity aligns you faster and can make you fail to attach to the ladder
                            charCtrl.SwitchThrusts();

                        //SetLadderStatus("Mounting ladder...", MyFontEnum.White);
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
                    var analogInput = Vector3.Zero;

                    if(controllingCharacter && readInput)
                        analogInput = MyAPIGateway.Input.GetPositionDelta();

                    if(analogInput.Y < 0) // crouch
                    {
                        if(!learned[4])
                            learned[4] = true;

                        ExitLadder(false);
                        return;
                    }

                    float move = MathHelper.Clamp((float)Math.Round(-analogInput.Z, 1), -1, 1); // forward/backward
                    float side = MathHelper.Clamp((float)Math.Round(analogInput.X, 1), -1, 1); // left/right
                    var alignVertical = ladderMatrix.Up.Dot(character.WorldMatrix.Up);

                    if(!loadedAllLearned)
                    {
                        bool allLearned = (learned[0] && learned[1] && learned[2] && learned[3] && learned[4]);

                        for(int i = 0; i < learned.Length; i++)
                        {
                            if(learnNotify[i] == null)
                                learnNotify[i] = MyAPIGateway.Utilities.CreateNotification("");

                            learnNotify[i].Text = (learned[i] ? LEARN_CHECK : LEARN_UNCHECK) + learnText[i];
                            learnNotify[i].Font = (learned[i] ? MyFontEnum.DarkBlue : MyFontEnum.White);
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

                    if(settings.relativeControls) // climbing relative to camera
                    {
                        float verticalModifier = MathHelper.Clamp((lookVertical + 0.65f) / 0.5f, -0.5f, 1.0f);

                        if(verticalModifier < 0)
                            verticalModifier *= 2;

                        move = (float)Math.Round(move * verticalModifier, 1);
                    }

                    if(analogInput.Y > 0) // jump
                    {
                        if(character.CurrentMovementState == MyCharacterMovementEnum.Jump) // this is still fine for avoiding jump as the character still is able to jump without needing feet on the ground
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

                    if(Math.Abs(side) > 0.0001f)
                    {
                        if(settings.relativeControls) // side dismounting relative to camera
                        {
                            var alignForward = ladderMatrix.Backward.Dot(character.WorldMatrix.Forward);

                            if(alignForward < 0)
                                side = -side;
                        }

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

                    if(Math.Abs(move) > 0.0001f)
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
                            if(slim == null || !(slim.FatBlock is IMyTerminalBlock) || !ladderIds.Contains(slim.FatBlock.BlockDefinition.SubtypeId))
                            {
                                dismounting = ALIGN_STEP;
                                SendLadderData(LadderAction.DISMOUNT);
                                return;
                            }
                        }

                        // on the floor and moving backwards makes you dismount
                        if(move < 0)
                        {
                            var feetStart = character.WorldMatrix.Translation + character.WorldMatrix.Up * 0.2; // a bit higher because the floor might clip through the character
                            var feetTarget = feetStart + character.WorldMatrix.Down * 0.3;
                            IHitInfo hit;

                            if(MyAPIGateway.Physics.CastRay(feetStart, feetTarget, out hit, COLLISSIONLAYER_NOCHARACTER))
                            {
                                // need to check the block under the ladder if it's anything but a ladder because "standing" stance occurs when character rests on its chest-sphere collision mesh too
                                var prevBlockWorldPos = ladderMatrix.Translation + ladderMatrix.Forward * offset.Z + (alignVertical > 0 ? ladderMatrix.Down : ladderMatrix.Up) * (halfY + 0.1f);
                                var prevBlockPos = ladder.CubeGrid.WorldToGridInteger(prevBlockWorldPos);
                                var slim = ladder.CubeGrid.GetCubeBlock(prevBlockPos);

                                // if it's not a ladder, check the distance and confirm your feet are close to its edge
                                if(slim == null || !(slim.FatBlock is IMyTerminalBlock) || !ladderIds.Contains(slim.FatBlock.BlockDefinition.SubtypeId))
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
                        }

                        // climbing on the ladder

                        if(!learned[1] && sprint)
                            learned[1] = true;

                        float speed = (characterDefinition == null ? (sprint ? VEL_SPRINT : VEL_CLIMB) : CHAR_SPEED_MUL * (sprint ? characterDefinition.MaxSprintSpeed : characterDefinition.MaxRunSpeed));

                        if(settings.clientPrediction)
                            character.Physics.LinearVelocity += (alignVertical > 0 ? ladderMatrix.Up : ladderMatrix.Down) * move * speed * TICKRATE;

                        if(!movingSideways)
                            LadderAnim(character, (move > 0 ? LadderAnimation.UP : LadderAnimation.DOWN));
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
                #endregion Ladder client logic
            }

            ExitLadder();
        }

        private void SendLadderData(LadderAction action, Vector3? vec = null, long? entId = null, float? climb = null, float? side = null, bool? sprint = null)
        {
            //if(MyAPIGateway.Multiplayer.IsServer)
            //    return;

            switch(action)
            {
                case LadderAction.CLIMB:
                    {
                        if(Math.Abs(climb.Value - myLadderStatus.climb) < 0.0001f && Math.Abs(side.Value - myLadderStatus.side) < 0.0001f && sprint.Value == myLadderStatus.sprint)
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

            int len = sizeof(byte) + sizeof(long) + sizeof(ulong);

            if(entId.HasValue)
                len += sizeof(long);

            if(vec.HasValue)
                len += sizeof(float) * 3;

            if(climb.HasValue && side.HasValue && sprint.HasValue)
                len += sizeof(float) * 2 + sizeof(bool);

            var bytes = new byte[len];
            len = 0;

            PacketHandler.AddToArray((byte)action, ref len, ref bytes);
            PacketHandler.AddToArray(character.EntityId, ref len, ref bytes);
            PacketHandler.AddToArray(MyAPIGateway.Multiplayer.MyId, ref len, ref bytes);

            if(entId.HasValue)
            {
                PacketHandler.AddToArray(entId.Value, ref len, ref bytes);
            }

            if(vec.HasValue)
            {
                PacketHandler.AddToArray(vec.Value, ref len, ref bytes);
            }

            if(climb.HasValue && side.HasValue && sprint.HasValue)
            {
                PacketHandler.AddToArray(climb.Value, ref len, ref bytes);
                PacketHandler.AddToArray(side.Value, ref len, ref bytes);
                PacketHandler.AddToArray(sprint.Value, ref len, ref bytes);
            }

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
                            MyAPIGateway.Utilities.ShowMessage(Log.modName, "Reloaded and re-saved config.");
                        else
                            MyAPIGateway.Utilities.ShowMessage(Log.modName, "Config created with the current settings.");

                        settings.Save();
                        return;
                    }

                    MyAPIGateway.Utilities.ShowMessage(Log.modName, "Available commands:");
                    MyAPIGateway.Utilities.ShowMessage("/ladder reload ", "reloads the config file.");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}
