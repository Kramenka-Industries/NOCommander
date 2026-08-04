using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

internal static class CommanderDestinationFormation
{
    private const int SlotsPerRing = 8;

    internal static GlobalPosition ApplyOffset(GlobalPosition center, int slotIndex, float spacing)
    {
        if (slotIndex <= 0 || spacing <= 0f)
        {
            return center;
        }

        int zeroBasedSlot = slotIndex - 1;
        int ring = zeroBasedSlot / SlotsPerRing + 1;
        int slotInRing = zeroBasedSlot % SlotsPerRing;
        float angle = slotInRing * (360f / SlotsPerRing);
        if ((ring & 1) == 0)
        {
            angle += 360f / (SlotsPerRing * 2f);
        }

        Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * (spacing * ring);
        return center + offset;
    }
}
