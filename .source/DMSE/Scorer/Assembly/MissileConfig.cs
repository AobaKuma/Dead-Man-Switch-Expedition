using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一枚導彈的裝配設定：彈體 + 每個槽位最多一個部件。提供有效數值計算。
    /// 同時用於導彈物品上的 CompMissileConfig、發射器上的「已裝填設定」、以及落點的 MissileIncoming。
    /// </summary>
    public class MissileConfig : IExposable
    {
        public MissileBodyDef body;
        public List<MissilePartDef> parts = new List<MissilePartDef>();

        public MissileConfig() { }

        public MissileConfig(MissileBodyDef body)
        {
            this.body = body;
        }

        /// <summary>
        /// 建立一個帶有彈體預設零件（<see cref="MissileBodyDef.defaultParts"/>）的設定。
        /// 只收與該彈體相容、且彈體確實有該槽位的部件；同類別取第一個。
        /// </summary>
        public static MissileConfig NewWithDefaults(MissileBodyDef body)
        {
            MissileConfig c = new MissileConfig(body);
            if (body?.defaultParts == null) { return c; }
            for (int i = 0; i < body.defaultParts.Count; i++)
            {
                MissilePartDef p = body.defaultParts[i];
                if (p == null || !p.CompatibleWith(body)) { continue; }
                if (c.PartFor(p.category) != null) { continue; }
                c.parts.Add(p);
            }
            return c;
        }

        public MissilePartDef PartFor(MissilePartCategory cat)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i] != null && parts[i].category == cat) { return parts[i]; }
            }
            return null;
        }

        public void SetPart(MissilePartCategory cat, MissilePartDef part)
        {
            parts.RemoveAll(p => p == null || p.category == cat);
            if (part != null) { parts.Add(part); }
        }

        // ---- 有效數值 ----
        public float ExplosionRadius => Mathf.Max(0.1f, Base(b => b.baseExplosionRadius) + SumF(p => p.explosionRadiusOffset));

        public DamageDef DamageDef
        {
            get
            {
                MissilePartDef wh = PartFor(MissilePartCategory.Warhead);
                if (wh != null && wh.damageDefOverride != null) { return wh.damageDefOverride; }
                return body != null && body.baseDamageDef != null ? body.baseDamageDef : DamageDefOf.Bomb;
            }
        }

        public int DamageAmount => Mathf.Max(1, Mathf.RoundToInt(Base(b => b.baseDamageAmount)) + SumI(p => p.damageAmountOffset));
        public float Scatter => Mathf.Max(0f, Base(b => b.baseScatter) + SumF(p => p.scatterOffset));
        public float WorldSpeedFactor => Mathf.Max(0.1f, Base(b => b.baseWorldSpeedFactor) + SumF(p => p.worldSpeedFactorOffset));

        /// <summary>有效燃料量 = 彈體基礎燃料 + 部件燃料加成。</summary>
        public float Fuel => Mathf.Max(0f, Base(b => b.baseFuel) + SumF(p => p.fuelOffset));

        /// <summary>有效比衝（推進效率）= 彈體基礎比衝 + 部件比衝加成。</summary>
        public float SpecificImpulse => Mathf.Max(0f, Base(b => b.baseSpecificImpulse) + SumF(p => p.specificImpulseOffset));

        /// <summary>
        /// 載荷容量係數 N。WarheadEffect/PayloadEffect 的效果以此縮放。
        /// </summary>
        public float PayloadCapacity => body != null ? body.payloadCapacity : 5f;

        /// <summary>各部件雷達截面倍率的乘積（誘餌酬載）。</summary>
        public float SignatureFactor
        {
            get
            {
                float f = 1f;
                for (int i = 0; i < parts.Count; i++) { if (parts[i] != null) { f *= Mathf.Max(0.01f, parts[i].signatureFactor); } }
                return f;
            }
        }

        /// <summary>各部件電子干擾強度的最大值（箔條／干擾絲）。</summary>
        public float JamStrength
        {
            get
            {
                float v = 0f;
                for (int i = 0; i < parts.Count; i++) { if (parts[i] != null) { v = Mathf.Max(v, parts[i].jamStrength); } }
                return Mathf.Clamp01(v);
            }
        }

        /// <summary>箔條／干擾絲總次數（各部件相加，飛行中只減不增）。</summary>
        public int ChaffCharges
        {
            get
            {
                int v = 0;
                for (int i = 0; i < parts.Count; i++) { if (parts[i] != null) { v += Mathf.Max(0, parts[i].chaffCharges); } }
                return v;
            }
        }

        /// <summary>
        /// 射程（世界 tile）= 燃料×比衝×係數 + 固定加成 + 戰鬥部增程格數，
        /// 最終乘以載荷係數（AirburstTungsten 減 25%）。0 = 不限。
        /// </summary>
        public int Range
        {
            get
            {
                float n = PayloadCapacity;
                float r = Fuel * SpecificImpulse * Base(b => b.rangePerFuelImpulse)
                          + Base(b => b.baseRange)
                          + SumI(p => p.rangeOffset);

                // 戰鬥部增程偏移（ExtendedFuel +N*2 格）
                MissilePartDef wh = PartFor(MissilePartCategory.Warhead);
                if (wh?.warheadEffect != null) { r += wh.warheadEffect.RangeOffset(n); }

                // 載荷射程係數（AirburstTungsten ×0.75）
                MissilePartDef pl = PartFor(MissilePartCategory.Payload);
                if (pl?.payloadEffect != null) { r *= pl.payloadEffect.RangeFactor(n); }

                return Mathf.Max(0, Mathf.RoundToInt(r));
            }
        }

        public bool Valid => body != null;

        /// <summary>
        /// 是否具備導引能力（導引槽部件提供終端導引，或彈體的內建導引標了 <see cref="MissileBodyDef.builtInTerminalGuidance"/>）。
        /// 慣性制導、未裝導引部件、或只有選點用內建導引的彈體為 false：無法跨圖層打擊，也無法追蹤移動目標。
        /// </summary>
        public bool HasTerminalGuidance
        {
            get
            {
                MissilePartDef guidance = PartFor(MissilePartCategory.Guidance);
                if (guidance != null) { return guidance.ProvidesTerminalGuidance; }
                return body != null && body.builtInTerminalGuidance
                    && body.builtInGuidance != null && body.builtInGuidance.TerminalGuidance;
            }
        }

        /// <summary>作為攔截彈時的命中率係數（彈體決定；沒有彈體為 1）。</summary>
        public float InterceptHitFactor => body != null ? Mathf.Max(0f, body.interceptHitFactor) : 1f;

        /// <summary>飛行中可更新終端位置的次數（由彈體決定，加上導引頭加成；無導引能力為 0）。</summary>
        public int TerminalUpdates => HasTerminalGuidance && body != null
            ? Mathf.Max(0, body.terminalUpdates + (TargetingGuidance?.ExtraTerminalUpdates ?? 0))
            : 0;

        /// <summary>導引頭是否以移動目標為對象（反艦）：航跡會更頻繁地重新瞄準。</summary>
        public bool TracksMobileTargets => TargetingGuidance?.TracksMobileTargets ?? false;

        /// <summary>
        /// 決定落點的導引頭：導引槽部件的 <see cref="MissilePartDef.guidanceType"/>，沒有時用彈體內建的
        /// <see cref="MissileBodyDef.builtInGuidance"/>；都沒有為 null（慣性）。只影響選目標，不影響導引能力。
        /// </summary>
        public GuidanceType TargetingGuidance
            => PartFor(MissilePartCategory.Guidance)?.guidanceType ?? body?.builtInGuidance;

        /// <summary>玩家發射時是否在抵達後由玩家手動選擇落點（電視制導）。</summary>
        public bool PlayerChoosesTarget => TargetingGuidance?.PlayerChoosesTarget ?? false;

        /// <summary>決定落點方式的名稱：導引部件、彈體（內建導引），或「慣性」。</summary>
        public string TargetingLabel
        {
            get
            {
                MissilePartDef part = PartFor(MissilePartCategory.Guidance);
                if (part != null) { return part.LabelCap; }
                if (body?.builtInGuidance != null) { return body.LabelCap; }
                return "DMSE.Guidance.Inertial".Translate();
            }
        }

        private float Base(System.Func<MissileBodyDef, float> sel) => body != null ? sel(body) : 0f;

        private float SumF(System.Func<MissilePartDef, float> sel)
        {
            float s = 0f;
            for (int i = 0; i < parts.Count; i++) { if (parts[i] != null) { s += sel(parts[i]); } }
            return s;
        }

        private int SumI(System.Func<MissilePartDef, int> sel)
        {
            int s = 0;
            for (int i = 0; i < parts.Count; i++) { if (parts[i] != null) { s += sel(parts[i]); } }
            return s;
        }

        public MissileConfig Clone()
        {
            MissileConfig c = new MissileConfig(body);
            c.parts = new List<MissilePartDef>(parts);
            return c;
        }

        public void CopyFrom(MissileConfig other)
        {
            body = other.body;
            parts = new List<MissilePartDef>(other.parts);
        }

        /// <summary>移除與目前彈體不相容的部件（更換彈體後清理用）。</summary>
        public void PruneIncompatible()
        {
            parts.RemoveAll(p => p == null || !p.CompatibleWith(body));
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref body, "body");
            Scribe_Collections.Look(ref parts, "parts", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && parts == null)
            {
                parts = new List<MissilePartDef>();
            }
        }
    }
}
