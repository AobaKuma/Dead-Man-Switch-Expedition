using RimWorld.Planet;
using UnityEngine;

namespace DMSE
{
    public interface ITravelingShip
    {
        PlanetTile start { get; set; }
        PlanetTile end { get; set; }
        float progress { get; }
        Vector3 DrawPos { get; }
    }
}