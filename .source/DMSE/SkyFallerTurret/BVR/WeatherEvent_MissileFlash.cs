using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈爆炸閃光：基於原版閃電閃光（<see cref="WeatherEvent_LightningFlash"/>），
    /// 但為暖色爆炸色調，且閃光強度與持續時間與導彈威力（intensity，0..1）成正比。不打雷、不傷害。
    /// </summary>
    public class WeatherEvent_MissileFlash : WeatherEvent_LightningFlash
    {
        private static readonly SkyColorSet ExplosionFlashColors = new SkyColorSet(
            new Color(1f, 0.85f, 0.6f), new Color(0.85f, 0.7f, 0.55f), new Color(1f, 0.8f, 0.5f), 1.2f);

        private readonly float intensity;

        public WeatherEvent_MissileFlash(Map map, float intensity, Vector2 shadow) : base(map)
        {
            this.intensity = Mathf.Clamp01(intensity);
            duration = Mathf.RoundToInt(Mathf.Lerp(12f, 70f, this.intensity)); // 威力越大、閃光越久。
            shadowVector = shadow;
        }

        public override SkyTarget SkyTarget => new SkyTarget(1f, ExplosionFlashColors, 1f, 1f);

        protected override float LightningBrightness => base.LightningBrightness * intensity; // 峰值亮度與威力成正比。

        public override void FireEvent() { } // 不打雷；爆炸音效由 GenExplosion 處理。
    }
}
