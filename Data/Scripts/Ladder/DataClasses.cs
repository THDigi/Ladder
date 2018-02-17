using System;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Digi.Ladder
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
                var bytes = BitConverter.GetBytes(character.EntityId);

                MyAPIGateway.Players.GetPlayers(null, p =>
                {
                    if(Vector3D.DistanceSquared(p.GetPosition(), position) <= LadderMod.STEP_RANGE_SQ)
                    {
                        MyAPIGateway.Multiplayer.SendMessageTo(LadderMod.PACKET_STEP, bytes, p.SteamUserId, false); // TODO mix this packet into the other packet?
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
}
