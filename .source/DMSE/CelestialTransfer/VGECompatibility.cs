using RimWorld;

namespace DMSE
{
    // VGE兼容层
    public static class VGECompatibility
    {
        private static System.Action<Building_GravEngine, float> _consumeFuelDelegate;

        public static void RegisterConsumeFuelDelegate(System.Action<Building_GravEngine, float> consumeFuel)
        {
            _consumeFuelDelegate = consumeFuel;
        }

        public static void ConsumeFuel(Building_GravEngine engine, float cost)
        {
            if (_consumeFuelDelegate != null)
            {
                _consumeFuelDelegate(engine, cost);
            }
            else
            {
                Verse.Log.Error("[DMSE] VGE compatibility layer not initialized. Cannot consume VGE fuel.");
            }
        }
    }
}
