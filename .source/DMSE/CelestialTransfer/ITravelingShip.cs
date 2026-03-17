using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public interface ITravelingShip
    {
        WorldObject WO { get; set; }
        float progress { get; set; }
        PlanetTile destinationTile { get; }
        void Setup(PlanetTile origin, PlanetTile destination);
        void Arrive();
    }
}