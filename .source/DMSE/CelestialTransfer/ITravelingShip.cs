using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public interface ITravelingShip
    {
        float progress { get; set; }
        PlanetTile destinationTile { get; }
        void Arrive();
    }
}