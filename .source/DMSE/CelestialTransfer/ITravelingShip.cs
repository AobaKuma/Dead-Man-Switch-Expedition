using RimWorld.Planet;
using UnityEngine;

namespace DMSE
{
    public interface ITravelingShip
    {
        PlanetTile start { get; set; }
        PlanetTile end { get; set; }
        Vector3 DrawPos { get; }
    }
}