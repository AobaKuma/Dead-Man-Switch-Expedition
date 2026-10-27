using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一種來襲物被攔截後的殘骸外觀與數量（docs/攔截殘骸特效_實作與技術美術計畫.md §6）。
    /// 來襲物 def 可用 <see cref="BVRTargetProps.debrisProfile"/> 指定；沒有指定時導彈用 <c>DMSE_InterceptDebris_Missile</c>，
    /// 空投艙用 <c>DMSE_InterceptDebris_DropPod</c>。彈道物理仍是 <see cref="InterceptDebrisPhysics.Default"/>（P6 再決定是否分 profile）。
    /// </summary>
    public class InterceptDebrisProfileDef : Def
    {
        // ---- A 層主殘骸 ----
        public IntRange majorCount = new IntRange(2, 4);
        public FloatRange majorMass = new FloatRange(1f, 2.5f);
        /// <summary>丟出一段斷裂彈體／艙體（畫來襲物自己的貼圖）的機率。</summary>
        public float hulkChance = 0.6f;
        /// <summary>主殘骸貼圖資料夾（Graphic_Random）：導彈碎片或艙體板件。</summary>
        public string majorTexPath = "Things/Effects/InterceptDebris/Major";
        /// <summary>齊射精簡版的主殘骸數（不丟斷裂彈體）。</summary>
        public int denseMajorCount = 1;

        // ---- B 層細碎片 ----
        public IntRange minorCount = new IntRange(14, 24);
        public FloatRange minorMass = new FloatRange(0.2f, 0.6f);
        public int denseMinorCount = 6;

        /// <summary>殘骸繼承的來襲物速度倍率（空投艙近乎垂直落下，較低）。</summary>
        public float inheritFactor = 1f;
        /// <summary>空爆時出火球（空投艙沒有彈頭，只有煙團）。</summary>
        public bool fireball = true;

        // ---- 顏色（Mote shader 上只能用淺色；Thing 用一般 shader，可用深色） ----
        /// <summary>主殘骸冷卻後的顏色（乘在熔渣塊貼圖上）。</summary>
        public Color majorTint = Color.white;
        /// <summary>斷裂彈體冷卻後的顏色。</summary>
        public Color hulkTint = new Color(0.62f, 0.58f, 0.55f, 1f);
        /// <summary>細碎片冷卻後的顏色。</summary>
        public Color minorTint = new Color(0.86f, 0.87f, 0.9f, 1f);

        private static InterceptDebrisProfileDef missile, dropPod;

        /// <summary>依攔截資料選 profile：來襲物 def 指定的優先，否則依種類取預設。</summary>
        public static InterceptDebrisProfileDef For(InterceptEventData data)
        {
            InterceptDebrisProfileDef own = data.payloadDef?.GetModExtension<BVRTargetProps>()?.debrisProfile;
            if (own != null) { return own; }
            if (data.payloadKind == InterceptPayloadKind.DropPod)
            {
                return dropPod ?? (dropPod = DefDatabase<InterceptDebrisProfileDef>.GetNamed("DMSE_InterceptDebris_DropPod"));
            }
            return missile ?? (missile = DefDatabase<InterceptDebrisProfileDef>.GetNamed("DMSE_InterceptDebris_Missile"));
        }
    }
}
