using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>導彈可自定義的部件類別（裝配槽位）。</summary>
    public enum MissilePartCategory
    {
        Warhead,    // 彈頭
        Guidance,   // 導引
        Propulsion, // 推進／射程
        Payload     // 特殊酬載
    }

    /// <summary>
    /// 導彈彈體定義。提供基礎參數，並決定此彈體開放哪些裝配槽位、抵達時使用哪個 incoming skyfaller。
    /// </summary>
    public class MissileBodyDef : Def
    {
        // 基礎參數
        public float baseExplosionRadius = 5f;
        public DamageDef baseDamageDef;          // 為 null 時於 MissileConfig 內回退為 Bomb
        public int baseDamageAmount = 100;
        public float baseScatter = 3f;           // 命中偏移半徑（格）
        public float baseWorldSpeedFactor = 1f;  // 世界地圖旅行速度倍率
        public int baseRange = 0;                // 額外固定射程加成（世界 tile）

        // 燃料與推進效率（決定射程）
        public float baseFuel = 100f;            // 彈體基礎燃料量
        public float baseSpecificImpulse = 3f;   // 基礎比衝（推進效率）
        public float rangePerFuelImpulse = 0.1f; // 射程換算係數：射程 ≈ 燃料 × 比衝 × 此係數

        // 裝配相關
        public int assembleWorkAmount = 600;     // 套用一次裝配變更所需工作量
        public float refundFraction = 0.5f;      // 移除/更換部件時退還的資源比例

        /// <summary>此彈體開放的裝配槽位。</summary>
        public List<MissilePartCategory> slots = new List<MissilePartCategory>();

        /// <summary>抵達目標後生成的 incoming skyfaller（thingClass 應為 DMSE.MissileIncoming）。</summary>
        public ThingDef incomingSkyfaller;

        public string uiIconPath;

        private Texture2D iconInt;
        public Texture2D Icon
        {
            get
            {
                if (iconInt == null && !uiIconPath.NullOrEmpty())
                {
                    iconInt = ContentFinder<Texture2D>.Get(uiIconPath, false);
                }
                return iconInt;
            }
        }
    }

    /// <summary>
    /// 導彈部件定義。屬於某個裝配槽位類別，提供對彈體基礎參數的數值修正。
    /// </summary>
    public class MissilePartDef : Def
    {
        public MissilePartCategory category;

        /// <summary>裝配此部件所需資源。</summary>
        public List<ThingDefCountClass> costList;

        // 數值修正（加算；guidance 的 scatterOffset 可為負以收緊散布）
        public float explosionRadiusOffset = 0f;
        public DamageDef damageDefOverride;       // 彈頭：覆寫傷害型別
        public int damageAmountOffset = 0;
        public float scatterOffset = 0f;          // 導引：負值降低命中偏移
        public float worldSpeedFactorOffset = 0f; // 推進：飛行速度
        public float specificImpulseOffset = 0f;  // 推進：比衝加成（推進效率，影響射程）
        public float fuelOffset = 0f;             // 燃料量加成
        public int rangeOffset = 0;               // 額外固定射程加成

        // 特殊酬載：落點額外效果
        public float payloadExplosionRadius = 0f;
        public DamageDef payloadDamageDef;
        public int payloadDamageAmount = 0;
        public ThingDef payloadSpawnThing;        // 例如毒氣／煙霧的填充物
        public float payloadSpawnChance = 1f;
        public int payloadSpawnCount = 1;

        /// <summary>相容彈體；null/空 = 相容所有具有此槽位的彈體。</summary>
        public List<MissileBodyDef> compatibleBodies;

        public List<ResearchProjectDef> researchPrerequisites;

        public string uiIconPath;

        private Texture2D iconInt;
        public Texture2D Icon
        {
            get
            {
                if (iconInt == null && !uiIconPath.NullOrEmpty())
                {
                    iconInt = ContentFinder<Texture2D>.Get(uiIconPath, false);
                }
                return iconInt;
            }
        }

        public bool ResearchSatisfied
        {
            get
            {
                if (researchPrerequisites == null) { return true; }
                for (int i = 0; i < researchPrerequisites.Count; i++)
                {
                    if (!researchPrerequisites[i].IsFinished) { return false; }
                }
                return true;
            }
        }

        public bool CompatibleWith(MissileBodyDef body)
        {
            if (body == null) { return false; }
            if (!body.slots.Contains(category)) { return false; }
            if (compatibleBodies != null && compatibleBodies.Count > 0 && !compatibleBodies.Contains(body)) { return false; }
            return true;
        }
    }
}
