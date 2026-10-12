using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一艘船（結構網路）上的推進、燃料、控制台勘查結果。由 <see cref="PileNetwork.Survey"/> 快取，
    /// ITab、確認視窗、整備檢查與各建築的檢視字串共用。只收 OccupiedRect 完整位於 footprint 內的建築
    /// （未被完整包含的會在分離時摧毀，不算船的一部分）。
    /// </summary>
    public class TransferShipSurvey
    {
        public class EngineEntry
        {
            public CompTransferEngine engine;

            public TransferEngineStatus status;

            public Thing blocker;

            public bool Operational => status == TransferEngineStatus.Operational;
        }

        public readonly PileNetwork network;

        public readonly List<EngineEntry> engines = new List<EngineEntry>();

        public readonly List<CompTransferReactor> reactors = new List<CompTransferReactor>();

        public readonly List<CompTransferConsole> consoles = new List<CompTransferConsole>();

        public readonly List<ITransferFuelSource> fuelSources = new List<ITransferFuelSource>();

        /// <summary>Σ 可用核融合爐的 supportedEngines。</summary>
        public int reactorCapacity;

        /// <summary>自身可運作、且需要核融合爐支援的引擎數。</summary>
        public int reactorDemand;

        /// <summary>Σ 可用引擎推力 T（不含一次性助推器）。</summary>
        public float totalThrust;

        /// <summary>Σ 可用一次性助推器推力；只計入離軌點火的推重比。</summary>
        public float boosterThrust;

        public int operationalBoosters;

        /// <summary>Σ(thrust × fuelFactor)，用來算推力加權燃耗倍率 η。</summary>
        public float thrustTimesFuelFactor;

        public float totalFuel;

        public int operationalEngines;

        /// <summary>船上的撞擊用引擎數（不論是否運作中）；決定撞擊坑規模與行星殺手結局。</summary>
        public int impactEngines;

        /// <summary>運作中的撞擊用引擎數；撞擊飛行的門檻（設計稿 §9）。</summary>
        public int operationalImpactEngines;

        public CompTransferConsole primaryConsole;

        private TransferShipSurvey(PileNetwork network)
        {
            this.network = network;
        }

        /// <summary>W：網路內所有樁的 Σ massWeight。</summary>
        public float MassWeight => network.massWeight;

        /// <summary>起飛（離軌點火）推重比：含一次性助推器。起飛門檻與離軌點火時間用這個。</summary>
        public float TWR => MassWeight > 0f ? (totalThrust + boosterThrust) / MassWeight : 0f;

        /// <summary>入軌點火推重比：助推器已在離軌點火後燒毀，只計引擎。</summary>
        public float ArrivalTWR => MassWeight > 0f ? totalThrust / MassWeight : 0f;

        /// <summary>推力加權燃耗倍率 η；沒有可用引擎時取 1。</summary>
        public float Eta => totalThrust > 0f ? thrustTimesFuelFactor / totalThrust : 1f;

        public EngineEntry EntryOf(CompTransferEngine engine)
        {
            for (int i = 0; i < engines.Count; i++)
            {
                if (engines[i].engine == engine)
                {
                    return engines[i];
                }
            }
            return null;
        }

        public static TransferShipSurvey Build(PileNetwork network)
        {
            TransferShipSurvey s = new TransferShipSurvey(network);
            Map map = network.map;
            HashSet<IntVec3> footprint = network.Footprint;

            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Building b = buildings[i];
                if (!PileNetwork.FullyInside(b, footprint))
                {
                    continue;
                }
                CompTransferEngine engine = b.GetComp<CompTransferEngine>();
                if (engine != null)
                {
                    Thing blocker;
                    TransferEngineStatus status = engine.SelfStatus(out blocker);
                    s.engines.Add(new EngineEntry { engine = engine, status = status, blocker = blocker });
                }
                CompTransferReactor reactor = b.GetComp<CompTransferReactor>();
                if (reactor != null)
                {
                    s.reactors.Add(reactor);
                }
                CompTransferConsole console = b.GetComp<CompTransferConsole>();
                if (console != null)
                {
                    s.consoles.Add(console);
                }
                ITransferFuelSource fuel = TransferFuelSources.TryGetSource(b);
                if (fuel != null)
                {
                    s.fuelSources.Add(fuel);
                }
            }

            // 依 thingIDNumber 排序，讓核融合爐支援與主控制台的分配在每次勘查間保持穩定
            s.engines.SortBy(e => e.engine.parent.thingIDNumber);
            s.consoles.SortBy(c => c.parent.thingIDNumber);

            for (int i = 0; i < s.reactors.Count; i++)
            {
                if (s.reactors[i].IsAvailable)
                {
                    s.reactorCapacity += s.reactors[i].Props.supportedEngines;
                }
            }
            int supported = 0;
            for (int i = 0; i < s.engines.Count; i++)
            {
                EngineEntry e = s.engines[i];
                if (!e.Operational || !e.engine.Props.requiresReactorSupport)
                {
                    continue;
                }
                s.reactorDemand++;
                if (supported < s.reactorCapacity)
                {
                    supported++;
                }
                else
                {
                    e.status = TransferEngineStatus.NoReactorSupport;
                }
            }

            for (int i = 0; i < s.engines.Count; i++)
            {
                EngineEntry e = s.engines[i];
                if (e.engine.Props.expendable)
                {
                    if (e.Operational)
                    {
                        s.operationalBoosters++;
                        s.boosterThrust += e.engine.Props.thrust;
                    }
                    continue;
                }
                if (e.engine.Props.impactCapable)
                {
                    s.impactEngines++;
                }
                if (!e.Operational)
                {
                    continue;
                }
                s.operationalEngines++;
                if (e.engine.Props.impactCapable)
                {
                    s.operationalImpactEngines++;
                }
                s.totalThrust += e.engine.Props.thrust;
                s.thrustTimesFuelFactor += e.engine.Props.thrust * e.engine.Props.fuelFactor;
            }

            s.totalFuel = TransferFuelSources.TotalAvailable(s.fuelSources);

            for (int i = 0; i < s.consoles.Count; i++)
            {
                if (s.consoles[i].IsAvailable)
                {
                    s.primaryConsole = s.consoles[i];
                    break;
                }
            }
            return s;
        }
    }
}
