using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移飛行的演出（掛在 <see cref="TransferEnvironmentExtension.presentation"/>）：離場音效、引擎持續音、鏡頭震動、
    /// 暖機火花、尾焰傷害與離場過場動畫的類別。沒掛的圖層用 <see cref="Space"/>（太空船：逆重船音效、震動、火花、尾焰燒傷）。
    /// 掛了的圖層照欄位的字面值：音效留空 = 不播。例如水下圖層的潛艇：沒有火箭聲與震動、推進器不會燒傷人。
    /// </summary>
    public class TransferPresentation
    {
        /// <summary>船體分離、離場動畫開始時的一次性音效；null = 不播。</summary>
        public SoundDef launchSound;

        /// <summary>引擎暖機（倒數）與點火期間的持續音；null = 不播。</summary>
        public SoundDef thrusterLoopSound;

        /// <summary>暖機、點火與離場動畫開頭的鏡頭震動倍率（0 = 不震）。</summary>
        public float cameraShake = 1f;

        /// <summary>暖機時噴口的火花與熱光。</summary>
        public bool nozzleSparks = true;

        /// <summary>點火期間尾焰區每次檢查造成的火焰傷害（0 = 不傷人）。</summary>
        public float exhaustDamage = 30f;

        /// <summary>離場過場動畫；必須是 <see cref="TransferTakeoffCutscene"/> 或其子類，建構子參數相同。null = <see cref="TransferTakeoffCutscene"/>。</summary>
        public Type takeoffCutsceneClass;

        /// <summary>
        /// 文字覆寫前綴（<see cref="TransferText"/>）：設了之後 <c>DMSE.Transfer.X</c> 先找 <c>前綴.X</c>。
        /// 例如 "DepthRim.Submarine" 讓潛艇的訊息、信件與介面改用航海用語；沒翻譯的鍵照用 DMSE 的。null = 不覆寫。
        /// </summary>
        public string textKeyPrefix;

        private static TransferPresentation space;

        /// <summary>太空（沒掛 presentation 的圖層）的預設演出。</summary>
        public static TransferPresentation Space => space ?? (space = new TransferPresentation
        {
            launchSound = SoundDefOf.Gravship_Launch,
            thrusterLoopSound = SoundDefOf.Gravship_RetroThruster_Loop
        });

        /// <summary>建立離場過場動畫。</summary>
        public TransferTakeoffCutscene MakeTakeoffCutscene(Map displayMap, UnityEngine.Vector3 center, Action onComplete, Map captureMap, HashSet<IntVec3> cells)
        {
            Type type = takeoffCutsceneClass ?? typeof(TransferTakeoffCutscene);
            return (TransferTakeoffCutscene)Activator.CreateInstance(type, displayMap, center, onComplete, captureMap, cells);
        }

        public IEnumerable<string> ConfigErrors()
        {
            if (takeoffCutsceneClass != null && !typeof(TransferTakeoffCutscene).IsAssignableFrom(takeoffCutsceneClass))
            {
                yield return $"presentation.takeoffCutsceneClass {takeoffCutsceneClass} must be {nameof(TransferTakeoffCutscene)} or a subclass.";
            }
            if (cameraShake < 0f || exhaustDamage < 0f)
            {
                yield return "presentation.cameraShake and exhaustDamage must not be negative.";
            }
        }
    }
}
