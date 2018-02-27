using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders;

namespace Digi.Ladder
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_AdvancedDoor), false, "LargeShipUsableLadderRetractable", "SmallShipUsableLadderRetractable")]
    public class LadderRetractable : LadderBlock { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TerminalBlock), false, "LargeShipUsableLadder", "SmallShipUsableLadder", "SmallShipUsableLadderSegment")]
    public class LadderStraight : LadderBlock { }

    public class LadderBlock : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void Close()
        {
            LadderMod.ladders.Remove(Entity.EntityId);
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                var block = (IMyTerminalBlock)Entity;

                if(block.CubeGrid.Physics == null)
                    return;

                // name needed for highlighting
                if(string.IsNullOrEmpty(Entity.Name))
                {
                    Entity.Name = LadderMod.LADDER_NAME_PREFIX + Entity.EntityId;
                    MyEntities.SetEntityName((MyEntity)Entity, true);
                }

                if(block.BlockDefinition.TypeId != typeof(MyObjectBuilder_AdvancedDoor))
                {
                    block.SetValueBool("ShowInTerminal", false);
                    block.SetValueBool("ShowInToolbarConfig", false);
                }

                LadderMod.ladders[Entity.EntityId] = block;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}
