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
