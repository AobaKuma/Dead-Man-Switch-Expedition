using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    public class CompProperties_SiloHatch : CompProperties
    {
        /// <summary>關閉的井蓋貼圖（與建築同中心、同 drawSize）。</summary>
        public string closedTexPath;

        /// <summary>打開的井蓋貼圖（翻過鉸鏈、躺在井外的蓋子；同 drawSize，位置由兩條鉸鏈線對齊）。</summary>
        public string openedTexPath;

        /// <summary>
        /// 關閉貼圖上鉸鏈的 Z 位置（以 drawSize 正規化，-0.5＝下緣、0.5＝上緣；北向）。
        /// 這條線就是建築上的鉸鏈：開關動畫時蓋子沿它翻轉。
        /// </summary>
        public float hingeZ = 0.39f;

        /// <summary>
        /// 打開貼圖上鉸鏈的 Z 位置（同上正規化）。繪製時把打開貼圖平移，使這條線落在 <see cref="hingeZ"/> 上，
        /// 所以打開的蓋子翻到井外（鉸鏈在上緣時就是井的北側），露出井口。
        /// </summary>
        public float openedHingeZ = -0.33f;

        /// <summary>完整開或關一次所需 tick。</summary>
        public int transitionTicks = 180;

        /// <summary>井蓋完全關閉時受到的傷害倍率。</summary>
        public float closedDamageFactor = 0.4f;

        /// <summary>發射後井蓋保持打開多久（tick）才自動關回去（「保持關閉」模式時）。</summary>
        public int holdOpenAfterLaunchTicks = 600;

        /// <summary>井蓋開始動作時的音效（可留空）。</summary>
        public SoundDef moveSound;

        public float altitudeOffset = 0.01f;

        public CompProperties_SiloHatch()
        {
            compClass = typeof(CompSiloHatch);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef)) { yield return e; }
            if (closedTexPath.NullOrEmpty() || openedTexPath.NullOrEmpty()) { yield return "CompProperties_SiloHatch needs closedTexPath and openedTexPath."; }
            if (transitionTicks < 1) { yield return "CompProperties_SiloHatch.transitionTicks must be >= 1."; }
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            foreach (StatDrawEntry e in base.SpecialDisplayStats(req)) { yield return e; }
            yield return new StatDrawEntry(StatCategoryDefOf.Building, "DMSE.SiloHatch.StatLabel".Translate(),
                closedDamageFactor.ToStringPercent(), "DMSE.SiloHatch.StatDesc".Translate(), 2000);
        }
    }

    /// <summary>
    /// 彈道導彈發射井的井蓋：開關動畫渲染、玩家「保持關閉」開關、發射前先開蓋、關閉時減傷、關蓋時不能裝配。
    ///
    /// 狀態：<see cref="keepClosed"/>（玩家設定，預設關閉）＋ <see cref="openProgress"/>（0＝全關、1＝全開，每 tick 逼近目標）。
    /// 目標開度：不保持關閉、有等待升空的發射（<see cref="launchesWaiting"/>），或升空後的開啟保留期內（<see cref="holdOpenUntil"/>）→ 開；否則關。
    ///
    /// 發射流程：<see cref="CompMissileLauncher_WorldTargeting.LaunchDeparture"/> 呼叫 <see cref="RequestLaunchOpen"/> 開始開蓋，
    /// 發射排程（<see cref="MapComponent_MissileLaunchQueue"/>）把升空彈扣住直到 <see cref="FullyOpen"/>，
    /// 升空彈生成時呼叫 <see cref="Notify_LaunchSpawned"/>：保留開啟一段時間後（保持關閉模式）再關回。
    /// 玩家單發、波次打擊與 NPC 代發都走 LaunchDeparture，所以都會先開蓋。
    /// 減傷只在完全關閉時生效；裝配只在完全打開時能做（<see cref="BlocksAssembly"/>）。
    /// </summary>
    [StaticConstructorOnStartup]
    public class CompSiloHatch : ThingComp
    {
        public CompProperties_SiloHatch Props => (CompProperties_SiloHatch)props;

        private bool keepClosed = true;
        private float openProgress;
        private int holdOpenUntil = -1;
        /// <summary>已下令、正等井蓋打開才升空的發射數。</summary>
        private int launchesWaiting;

        private Material closedMat;
        private Material openedMat;

        public bool FullyClosed => openProgress <= 0f;
        public bool FullyOpen => openProgress >= 1f;

        /// <summary>井蓋沒有完全打開時不能裝配（小人要從井口吊裝彈體）。</summary>
        public bool BlocksAssembly => !FullyOpen;

        /// <summary>正在為發射開蓋（等待升空中）。</summary>
        public bool OpeningForLaunch => launchesWaiting > 0;

        private bool WantOpen => !keepClosed || launchesWaiting > 0 || Find.TickManager.TicksGame < holdOpenUntil;

        /// <summary>發射器上的井蓋（沒有則為 null）。</summary>
        public static CompSiloHatch For(Thing launcher) => (launcher as ThingWithComps)?.GetComp<CompSiloHatch>();

        /// <summary>從目前開度完全打開還要幾 tick（已全開 = 0）。</summary>
        public int TicksUntilOpen => FullyOpen ? 0 : Mathf.CeilToInt((1f - openProgress) * Props.transitionTicks) + 1;

        public override void CompTick()
        {
            base.CompTick();
            float target = WantOpen ? 1f : 0f;
            if (openProgress == target) { return; }
            if (openProgress == 0f || openProgress == 1f) { PlayMoveSound(); }
            openProgress = Mathf.MoveTowards(openProgress, target, 1f / Props.transitionTicks);
        }

        /// <summary>下令發射：開始開蓋（升空彈由發射排程扣住，直到全開）；回傳預估還要幾 tick 才能升空。</summary>
        public int RequestLaunchOpen()
        {
            launchesWaiting++;
            return TicksUntilOpen;
        }

        /// <summary>發射排程確認仍有等待中的發射（讀檔後計數遺失時補回）。</summary>
        public void EnsureLaunchOpen()
        {
            if (launchesWaiting <= 0) { launchesWaiting = 1; }
        }

        /// <summary>升空彈已生成（或排隊中的發射被取消）：不再等待；保持關閉模式下，保留開啟一段時間後關回。</summary>
        public void Notify_LaunchSpawned()
        {
            if (launchesWaiting > 0) { launchesWaiting--; }
            holdOpenUntil = Find.TickManager.TicksGame + Props.holdOpenAfterLaunchTicks;
        }

        private void PlayMoveSound()
        {
            if (Props.moveSound != null && parent.Spawned && parent.Map == Find.CurrentMap)
            {
                Props.moveSound.PlayOneShot(new TargetInfo(parent.Position, parent.Map));
            }
        }

        public override void PostPreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            base.PostPreApplyDamage(ref dinfo, out absorbed);
            if (absorbed || !parent.Spawned || !FullyClosed) { return; }
            dinfo.SetAmount(dinfo.Amount * Props.closedDamageFactor);
        }

        // ---------------- 渲染 ----------------

        public override void PostDraw()
        {
            base.PostDraw();
            if (closedMat == null)
            {
                Vector2 size = parent.def.graphicData.drawSize;
                closedMat = GraphicDatabase.Get<Graphic_Single>(Props.closedTexPath, ShaderDatabase.Cutout, size, Color.white).MatSingle;
                openedMat = GraphicDatabase.Get<Graphic_Single>(Props.openedTexPath, ShaderDatabase.Cutout, size, Color.white).MatSingle;
            }

            // 前半段：關閉的蓋子朝鉸鏈壓扁（cos 0→90°，蓋子立起）；後半段：打開的蓋子從鉸鏈往井外攤平。
            float p = openProgress;
            bool firstHalf = p < 0.5f;
            float s = firstHalf ? Mathf.Cos(p * Mathf.PI) : -Mathf.Cos(p * Mathf.PI);
            if (s <= 0.02f) { return; }
            if (firstHalf) { DrawFlipped(closedMat, s, Props.hingeZ); }
            else { DrawFlipped(openedMat, s, Props.openedHingeZ); }
        }

        /// <param name="spriteHingeZ">貼圖上鉸鏈的正規化 Z；畫出來時這條線永遠落在建築的鉸鏈（<see cref="CompProperties_SiloHatch.hingeZ"/>）上。</param>
        private void DrawFlipped(Material mat, float scaleZ, float spriteHingeZ)
        {
            Vector2 size = parent.def.graphicData.drawSize;
            Vector3 pos = parent.DrawPos;
            pos.y = AltitudeLayer.BuildingOnTop.AltitudeFor() + Props.altitudeOffset;
            Quaternion rot = Quaternion.Euler(0f, parent.Rotation.AsAngle, 0f);
            // 貼圖上 z＝spriteHingeZ 的點縮放後落在 hingeZ：spriteHingeZ·H·s + offset = hingeZ·H。
            Vector3 localOffset = new Vector3(0f, 0f, (Props.hingeZ - spriteHingeZ * scaleZ) * size.y);
            Matrix4x4 matrix = Matrix4x4.TRS(pos + rot * localOffset, rot, new Vector3(size.x, 1f, size.y * scaleZ));
            Graphics.DrawMesh(MeshPool.GridPlane(Vector2.one, false), matrix, mat, 0);
        }

        // ---------------- UI ----------------

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) { yield return g; }
            if (parent.Faction != Faction.OfPlayer) { yield break; }
            yield return new Command_Toggle
            {
                defaultLabel = "DMSE.SiloHatch.KeepClosed".Translate(),
                defaultDesc = "DMSE.SiloHatch.KeepClosedDesc".Translate(Props.closedDamageFactor.ToStringPercent()),
                icon = ContentFinder<Texture2D>.Get(keepClosed ? Props.closedTexPath : Props.openedTexPath),
                isActive = () => keepClosed,
                toggleAction = () =>
                {
                    keepClosed = !keepClosed;
                    if (!keepClosed) { holdOpenUntil = -1; }
                }
            };
        }

        public override string CompInspectStringExtra()
        {
            string state;
            if (FullyClosed)
            {
                state = "DMSE.SiloHatch.Closed".Translate(Props.closedDamageFactor.ToStringPercent());
            }
            else if (FullyOpen)
            {
                state = "DMSE.SiloHatch.Open".Translate();
            }
            else
            {
                state = (OpeningForLaunch ? "DMSE.SiloHatch.OpeningForLaunch" : WantOpen ? "DMSE.SiloHatch.Opening" : "DMSE.SiloHatch.Closing").Translate();
            }
            if (BlocksAssembly && parent.TryGetComp<CompMissileConfig>() is CompMissileConfig cfg && cfg.assemblyConfirmed && cfg.NeedsAssembly)
            {
                state += "\n" + "DMSE.SiloHatch.AssemblyBlocked".Translate();
            }
            return state;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref keepClosed, "siloHatchKeepClosed", true);
            Scribe_Values.Look(ref openProgress, "siloHatchOpenProgress", 0f);
            Scribe_Values.Look(ref holdOpenUntil, "siloHatchHoldOpenUntil", -1);
            Scribe_Values.Look(ref launchesWaiting, "siloHatchLaunchesWaiting", 0);
        }
    }
}
