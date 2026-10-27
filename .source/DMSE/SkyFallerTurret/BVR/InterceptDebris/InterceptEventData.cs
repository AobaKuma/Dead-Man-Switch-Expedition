using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public enum InterceptPayloadKind { Other, Missile, DropPod }

    /// <summary>
    /// 一次攔截成功的表現資料（docs/攔截殘骸特效_實作與技術美術計畫.md §2–§3）。
    /// 必須在目標被 RemoveTarget / Discard / SpawnDebris 清掉之前以 <see cref="From"/> 擷取，因為要讀來襲物的 def、方位與速度。
    /// </summary>
    public struct InterceptEventData
    {
        public bool terminal;
        public IntVec3 landing;
        /// <summary>來襲物的進入方位（0 = 北、順時針；「從哪裡來」）。</summary>
        public float heading;
        /// <summary>false = 沒有來襲物可取方位，heading 為退回值。</summary>
        public bool hasHeading;
        /// <summary>爆心的地面位置（可在圖外）。</summary>
        public Vector3 burstGround;
        /// <summary>爆心的假想高度（格）。</summary>
        public float burstHeight;
        /// <summary>
        /// 遠距攔截（只有末端）：爆心是看不見的虛擬點（畫面位置在圖外或邊緣帶），不畫爆炸，殘骸從虛擬點飛進畫面時淡入。
        /// </summary>
        public bool remote;
        public InterceptPayloadKind payloadKind;
        public ThingDef payloadDef;
        /// <summary>來襲物的平均速度（格/tick），殘骸依比例繼承。</summary>
        public float targetSpeed;
        public SoundDef sound;
        /// <summary>純視覺亂數的種子（Rand.PushState）。</summary>
        public int seed;

        /// <summary>爆心的畫面位置（高度畫成往畫面上方偏移）。</summary>
        public Vector3 BurstDrawPos(InterceptDebrisPhysics p) => burstGround + new Vector3(0f, 0f, burstHeight * p.heightScale);

        /// <summary>
        /// 由 BVR 目標建立。末端攔截傳 CIWS 本輪的掃射點 <paramref name="aimCell"/> 與迫近進度 <paramref name="approach"/>；
        /// 中過程攔截傳 <see cref="IntVec3.Invalid"/>，攔截點放在入場邊緣外。
        /// </summary>
        public static InterceptEventData From(BVRTarget target, Map map, bool terminal, IntVec3 aimCell, float approach = 0f,
            SoundDef sound = null, InterceptDebrisPhysics physics = null)
        {
            InterceptDebrisPhysics p = physics ?? InterceptDebrisPhysics.Default;
            Thing payload = target?.Payload;
            IntVec3 landing = target != null ? target.position : map.Center;
            int seed = Gen.HashCombineInt(target != null ? target.id : 0, Find.TickManager.TicksGame);
            return Build(payload, landing, map.Size, map.Center, terminal, aimCell, approach, sound, seed, p);
        }

        /// <summary>沒有 BVR 目標時（舊呼叫端、除錯）：只有落點，方位退回「地圖中心 → 落點」。</summary>
        public static InterceptEventData FromCell(Map map, IntVec3 landing, bool terminal, SoundDef sound = null)
        {
            int seed = Gen.HashCombineInt(landing.GetHashCode(), Find.TickManager.TicksGame);
            return Build(null, landing, map.Size, map.Center, terminal, IntVec3.Invalid, 0f, sound, seed, InterceptDebrisPhysics.Default);
        }

        /// <summary>不依賴 Map 的建構（測試用）。</summary>
        public static InterceptEventData Build(Thing payload, IntVec3 landing, IntVec3 mapSize, IntVec3 mapCenter, bool terminal,
            IntVec3 aimCell, float approach, SoundDef sound, int seed, InterceptDebrisPhysics p, float? headingOverride = null)
        {
            Vector3 landingPos = landing.ToVector3Shifted();
            landingPos.y = 0f;
            Vector3 center = mapCenter.ToVector3Shifted();
            center.y = 0f;
            // 落點恰在地圖中心又沒有來襲物時，HeadingOf(0) = 北，仍是合理的退回值。
            bool hasHeading = true;
            float heading = headingOverride ?? InterceptGeometry.IncomingHeading(payload, landingPos, center, out hasHeading);

            InterceptEventData data = new InterceptEventData
            {
                terminal = terminal,
                landing = landing,
                heading = heading,
                hasHeading = hasHeading,
                // 舊存檔的 skyfaller 導彈也是 Skyfaller，先判斷導彈。
                payloadKind = payload is IIncomingMissile ? InterceptPayloadKind.Missile
                    : payload is Skyfaller ? InterceptPayloadKind.DropPod
                    : InterceptPayloadKind.Other,
                payloadDef = payload?.def,
                targetSpeed = InterceptGeometry.AverageSpeed(payload),
                sound = sound,
                seed = seed
            };

            Rand.PushState(seed);
            try
            {
                if (!terminal)
                {
                    data.burstGround = InterceptGeometry.MidcourseBurstGround(mapSize, landingPos, heading, p, out data.burstHeight);
                }
                else
                {
                    Vector3 ground;
                    float height;
                    if (approach < p.terminalRemoteApproach)
                    {
                        // 剛進入末端窗口：目標還在圖外，爆心放在入場邊緣外的虛擬點（CIWS 掃射點被夾在圖內，不用）。
                        ground = InterceptGeometry.RemoteBurstGround(mapSize, landingPos, heading, approach, data.targetSpeed, p, out height);
                    }
                    else if (aimCell.IsValid)
                    {
                        ground = InterceptGeometry.TerminalBurstGround(aimCell.ToVector3Shifted(), heading, approach, p, out height);
                    }
                    else
                    {
                        // 末端但沒有掃射點：同 CIWS 預設（aimHeightFar 1 → aimHeightNear 0.5），沿航線從入場邊緣迫近到中段。
                        Vector3 edge = MissileMapEntryUtility.EdgePoint(mapSize, landingPos, heading);
                        Vector3 mid = landingPos + (edge - landingPos) * Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(approach));
                        ground = InterceptGeometry.TerminalBurstGround(mid, heading, approach, p, out height);
                    }
                    // 畫面位置看不見（圖外或邊緣帶，例如北側入場時高度偏移超出北緣）：不再夾回圖內貼邊爆炸，改為遠距。
                    data.remote = InterceptGeometry.DrawHidden(ground, height, mapSize, p);
                    data.burstGround = ground;
                    data.burstHeight = height;
                }
            }
            finally
            {
                Rand.PopState();
            }
            return data;
        }
    }
}
