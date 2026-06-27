using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 掛在「發射平台 / 儲存架 / 儲存建築」ThingDef 上的擴充，宣告此建築處理哪一階導彈、
    /// 採用哪種發射方式、以及彈容量。供裝填驗證、ITab、WorkGiver 等共用判定使用。
    ///
    /// 注意：垂直發射的「不可上重力船 + 單一朝向」限制由 XML 達成（rotatable=false 並掛
    /// 原版 PlaceWorker_InvalidOverSubstructure），此擴充僅描述資料，不重複實作該限制。
    /// 參見抽象母 Def DMSE_VerticalLauncherBase / DMSE_TiltLauncherBase。
    /// </summary>
    public class MissileLauncherExtension : DefModExtension
    {
        /// <summary>此平台接受的導彈尺寸分級。</summary>
        public MissileSizeClass sizeClass = MissileSizeClass.Medium;

        /// <summary>此平台的發射方式（Tilt 或 Vertical；不應為 Both）。</summary>
        public MissileLaunchMode launchMode = MissileLaunchMode.Tilt;

        /// <summary>彈容量（可裝填／儲存的導彈數量）。</summary>
        public int capacity = 1;

        /// <summary>是否為儲存類建築（彈架／彈藥庫），而非發射平台。</summary>
        public bool isStorage = false;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }

            if (launchMode == MissileLaunchMode.Both)
            {
                yield return "MissileLauncherExtension.launchMode 不應為 Both（平台必須是 Tilt 或 Vertical）。";
            }
            if (capacity < 1)
            {
                yield return "MissileLauncherExtension.capacity 必須 >= 1。";
            }
        }
    }

    /// <summary>導彈分級／發射相容性的共用判定。</summary>
    public static class MissileTaxonomy
    {
        /// <summary>彈體的發射方式是否相容於某平台的發射方式。</summary>
        public static bool LaunchModeCompatible(MissileLaunchMode bodyMode, MissileLaunchMode platformMode)
        {
            if (bodyMode == MissileLaunchMode.Both)
            {
                return true;
            }
            return bodyMode == platformMode;
        }

        /// <summary>此平台（含 MissileLauncherExtension）能否裝填／儲存此彈體：分級需相符且發射方式相容。</summary>
        public static bool PlatformAccepts(MissileLauncherExtension ext, MissileBodyDef body)
        {
            if (ext == null || body == null)
            {
                return false;
            }
            if (ext.sizeClass != body.sizeClass)
            {
                return false;
            }
            return LaunchModeCompatible(body.launchMode, ext.launchMode);
        }

        /// <summary>便利方法：直接由建築 def 取得擴充並判定。</summary>
        public static bool PlatformAccepts(ThingDef platformDef, MissileBodyDef body)
        {
            return PlatformAccepts(platformDef?.GetModExtension<MissileLauncherExtension>(), body);
        }
    }
}
