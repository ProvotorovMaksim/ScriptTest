using System;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace SpaceEngineers.Libs.TaskBusConsumer
{
    internal static class InventoryTransfer
    {
        public static MyFixedPoint AmountThatFits(IMyInventory destination, MyInventoryItem item, double amount)
        {
            var quantity = (MyFixedPoint)Math.Min(amount, (double)item.Amount);
            if (destination.CanItemsBeAdded(quantity, item.Type))
                return quantity;

            // Probe capacity without moving cargo.
            double lower = 0;
            double upper = (double)quantity;
            for (int i = 0; i < 32 && upper - lower > 0.000001; i++)
            {
                double middle = lower + (upper - lower) * 0.5;
                if (destination.CanItemsBeAdded((MyFixedPoint)middle, item.Type))
                    lower = middle;
                else
                    upper = middle;
            }
            return (MyFixedPoint)lower;
        }
    }
}
