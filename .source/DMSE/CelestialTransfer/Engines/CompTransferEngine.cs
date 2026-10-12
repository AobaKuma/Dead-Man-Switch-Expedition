using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移引擎設定（設計稿 §5-1）。不繼承 CompProperties_GravshipThruster（那會去找重力引擎連結），
    /// 但尾焰區與火焰外觀欄位沿用原版名稱，方便直接搬 XML。
    /// </summary>
    public class CompProperties_TransferEngine : CompProperties
    {
        /// <summary>推力（抽象單位）。</summary>
        public float thrust = 1f;

        /// <summary>燃耗倍率：1.0 = 化學推進基準，越低越省。</summary>
        public float fuelFactor = 1f;

        /// <summary>是否需要同一艘船上的核融合爐支援（熱核推進器用）。</summary>
        public bool requiresReactorSupport;

        /// <summary>計入撞擊飛行（設計稿 §9）所需的引擎數（熱核推進器）。</summary>
        public bool impactCapable;

        /// <summary>
        /// 一次性助推器（火箭助推器）：推力只計入離軌點火的推重比，不耗船上燃料、不算可用引擎數；
        /// 起飛時點燃，離軌點火結束後燒毀。
        /// </summary>
        public bool expendable;

        public IntVec3 exclusionAreaSize;

        public IntVec3 exclusionAreaOffset;

        public float flameSize;

        public List<Vector3> flameOffsetsPerDirection = new List<Vector3>
        {
            Vector3.zero,
            Vector3.zero,
            Vector3.zero,
            Vector3.zero
        };

#pragma warning disable CS0649 // 由 XML 指派
        private ShaderTypeDef flameShaderType;
#pragma warning restore CS0649

        public List<ShaderParameter> flameShaderParameters = new List<ShaderParameter>();

        public CompProperties_GravshipThruster.ExhaustSettings exhaustSettings = new CompProperties_GravshipThruster.ExhaustSettings();

        public ShaderTypeDef FlameShaderType => flameShaderType ?? ShaderTypeDefOf.MoteGlow;

        public CompProperties_TransferEngine()
        {
            compClass = typeof(CompTransferEngine);
        }

        /// <summary>尾焰區格子，公式同原版 CompProperties_GravshipThruster.GetExclusionZone。</summary>
        public void GetExclusionZone(IntVec3 pos, Rot4 rot, List<IntVec3> outCells)
        {
            outCells.Clear();
            for (int i = 0; i < exclusionAreaSize.x; i++)
            {
                for (int j = 0; j < exclusionAreaSize.z; j++)
                {
                    outCells.Add(pos + new IntVec3(i, 0, j).RotatedBy(rot) + exclusionAreaOffset.RotatedBy(rot));
                }
            }
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (thrust <= 0f)
            {
                yield return "thrust must be positive.";
            }
            if (fuelFactor <= 0f)
            {
                yield return "fuelFactor must be positive.";
            }
        }
    }

    public enum TransferEngineStatus
    {
        Operational,
        NotOnShip,
        BrokenDown,
        SwitchedOff,
        NoPower,
        ExhaustBlocked,
        NoReactorSupport
    }

    /// <summary>
    /// 轉移引擎（化學轉移引擎、熱核推進器）。是否可用除了自身狀態，還取決於所屬船的核融合爐支援額度，
    /// 那部分由 <see cref="TransferShipSurvey"/> 在網路層級分配。
    /// </summary>
    public class CompTransferEngine : ThingComp
    {
        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        private bool fired;

        public CompProperties_TransferEngine Props => (CompProperties_TransferEngine)props;

        /// <summary>一次性助推器已在起飛時點燃；離軌點火結束時由 <see cref="WorldObject_TransferShip"/> 燒毀。</summary>
        public bool Fired => fired;

        public void MarkFired()
        {
            fired = true;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref fired, "transferBoosterFired", defaultValue: false);
        }

        public void GetExclusionZone(List<IntVec3> outCells)
        {
            Props.GetExclusionZone(parent.Position, parent.Rotation, outCells);
        }

        /// <summary>全推力時的火焰長度（同原版：建築寬 × flameSize）。</summary>
        public float FlameLength => parent.def.size.x * Props.flameSize;

        /// <summary>噴口位置（火焰起點）；火焰、排氣與暖機火花共用。</summary>
        public Vector3 NozzlePos
        {
            get
            {
                Rot4 rot = parent.Rotation;
                List<Vector3> offsets = Props.flameOffsetsPerDirection;
                Vector3 offset = offsets != null && offsets.Count > rot.AsInt ? rot.AsQuat * offsets[rot.AsInt] : Vector3.zero;
                return GenThing.TrueCenter(parent.Position, rot, parent.def.size, 0f) - rot.AsIntVec3.ToVector3() * (parent.def.size.z * 0.5f) + offset;
            }
        }

        /// <summary>長度為 length 的火焰中心（火焰從噴口往後延伸）。</summary>
        public Vector3 FlameCenter(float length)
        {
            return NozzlePos - parent.Rotation.AsIntVec3.ToVector3() * (length * 0.5f);
        }

        /// <summary>
        /// 設計稿 §5-1：尾焰區內有 Impassable 或 fillPercent ≥ 0.5 的建築（含天然岩）即受阻。地板不擋。
        /// </summary>
        public static bool IsExhaustBlocked(ThingDef def, Map map, IntVec3 pos, Rot4 rot, Thing self, out Thing blocker)
        {
            blocker = null;
            CompProperties_TransferEngine props = def.GetCompProperties<CompProperties_TransferEngine>();
            if (props == null)
            {
                return false;
            }
            props.GetExclusionZone(pos, rot, tmpCells);
            for (int i = 0; i < tmpCells.Count; i++)
            {
                IntVec3 c = tmpCells[i];
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing t = things[j];
                    if (t == self || t.def.category != ThingCategory.Building || t is Plant)
                    {
                        continue;
                    }
                    if (t.def.passability == Traversability.Impassable || t.def.fillPercent >= 0.5f)
                    {
                        blocker = t;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>不含「是否在船上」與核融合爐支援的自身狀態。</summary>
        public TransferEngineStatus SelfStatus(out Thing blocker)
        {
            blocker = null;
            if (!parent.Spawned)
            {
                return TransferEngineStatus.NotOnShip;
            }
            CompBreakdownable breakdownable = parent.GetComp<CompBreakdownable>();
            if (breakdownable != null && breakdownable.BrokenDown)
            {
                return TransferEngineStatus.BrokenDown;
            }
            CompFlickable flickable = parent.GetComp<CompFlickable>();
            if (flickable != null && !flickable.SwitchIsOn)
            {
                return TransferEngineStatus.SwitchedOff;
            }
            CompPowerTrader power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return TransferEngineStatus.NoPower;
            }
            if (IsExhaustBlocked(parent.def, parent.Map, parent.Position, parent.Rotation, parent, out blocker))
            {
                return TransferEngineStatus.ExhaustBlocked;
            }
            return TransferEngineStatus.Operational;
        }

        /// <summary>完整狀態：所屬船的勘查結果；不在任何船體範圍內時為 NotOnShip。</summary>
        public TransferEngineStatus CurrentStatus(out Thing blocker)
        {
            blocker = null;
            PileNetwork net = parent.Spawned ? MapComponent_PileNetwork.For(parent.Map)?.NetworkContaining(parent) : null;
            TransferShipSurvey.EngineEntry entry = net?.Survey.EntryOf(this);
            if (entry == null)
            {
                return TransferEngineStatus.NotOnShip;
            }
            blocker = entry.blocker;
            return entry.status;
        }

        public override string CompInspectStringExtra()
        {
            StringBuilder sb = new StringBuilder();
            if (Props.expendable)
            {
                sb.Append("DMSE.Transfer.Engine.BoosterStats".Translate(Props.thrust.ToString("0.#")));
            }
            else
            {
                sb.Append("DMSE.Transfer.Engine.Stats".Translate(Props.thrust.ToString("0.#"), Props.fuelFactor.ToStringPercent()));
            }
            if (Props.requiresReactorSupport)
            {
                sb.AppendLine();
                sb.Append("DMSE.Transfer.Engine.RequiresReactor".Translate());
            }
            if (parent.Spawned)
            {
                Thing blocker;
                TransferEngineStatus status = CurrentStatus(out blocker);
                sb.AppendLine();
                string label = StatusLabel(status, blocker);
                sb.Append(status == TransferEngineStatus.Operational ? label : label.Colorize(ColorLibrary.RedReadable));
            }
            return sb.ToString();
        }

        public static string StatusLabel(TransferEngineStatus status, Thing blocker)
        {
            switch (status)
            {
                case TransferEngineStatus.Operational:
                    return "DMSE.Transfer.Engine.Status.Operational".Translate();
                case TransferEngineStatus.NotOnShip:
                    return "DMSE.Transfer.Engine.Status.NotOnShip".Translate();
                case TransferEngineStatus.BrokenDown:
                    return "DMSE.Transfer.Engine.Status.BrokenDown".Translate();
                case TransferEngineStatus.SwitchedOff:
                    return "DMSE.Transfer.Engine.Status.SwitchedOff".Translate();
                case TransferEngineStatus.NoPower:
                    return "DMSE.Transfer.Engine.Status.NoPower".Translate();
                case TransferEngineStatus.ExhaustBlocked:
                    return "DMSE.Transfer.Engine.Status.ExhaustBlocked".Translate(blocker != null ? blocker.LabelShort : "?");
                case TransferEngineStatus.NoReactorSupport:
                    return "DMSE.Transfer.Engine.Status.NoReactorSupport".Translate();
                default:
                    return status.ToString();
            }
        }
    }
}
