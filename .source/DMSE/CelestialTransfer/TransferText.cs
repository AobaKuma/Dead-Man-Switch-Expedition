using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移飛行文字的圖層覆寫（2026-11-11）：DMSE 的文字是太空船用語（起飛、離軌點火、滑行、降落……）。
    /// 圖層的 <see cref="TransferPresentation.textKeyPrefix"/> 有設時，<c>DMSE.Transfer.X</c> 先找 <c>前綴.X</c>，
    /// 有翻譯就用它，沒有就退回原本的鍵。例如 DepthRim 的潛艇把「入軌點火」改成「減速進港」，太空船不受影響。
    /// <para>用法：<c>TransferText.Key("DMSE.Transfer.Phase.Coast", map).Translate()</c>，鍵換掉之後照原樣翻譯與帶參數。</para>
    /// </summary>
    public static class TransferText
    {
        public const string BasePrefix = "DMSE.Transfer.";

        /// <summary>依演出的文字前綴換鍵；沒有前綴、鍵不是轉移文字、或覆寫鍵沒有翻譯時傳回原鍵。</summary>
        public static string Key(string key, TransferPresentation presentation)
        {
            string prefix = presentation?.textKeyPrefix;
            if (prefix.NullOrEmpty() || key == null || !key.StartsWith(BasePrefix))
            {
                return key;
            }
            string over = prefix + "." + key.Substring(BasePrefix.Length);
            return over.CanTranslate() ? over : key;
        }

        public static string Key(string key, PlanetLayerDef layer) => Key(key, Presentation(layer));

        public static string Key(string key, PlanetTile tile) => Key(key, tile.Valid ? tile.LayerDef : null);

        public static string Key(string key, Map map) => Key(key, map != null && map.Tile.Valid ? map.Tile.LayerDef : null);

        public static string Key(string key, WorldObject worldObject) => Key(key, worldObject != null ? worldObject.Tile : PlanetTile.Invalid);

        public static string Key(string key, Thing thing) => Key(key, thing?.MapHeld);

        public static string Key(string key, CompTransferConsole console) => Key(key, console?.parent);

        private static TransferPresentation ambient;

        /// <summary>用 <see cref="Using(Map)"/> 設定的目前圖層換鍵：給沒有地圖可查的靜態介面工具（估算表、引擎狀態標籤）用。</summary>
        public static string Key(string key) => Key(key, ambient);

        /// <summary>在 using 區塊內讓 <see cref="Key(string)"/> 使用這張地圖的圖層，離開時還原（巢狀也可以）。</summary>
        public static AmbientScope Using(Map map)
        {
            return new AmbientScope(map != null && map.Tile.Valid ? Presentation(map.Tile.LayerDef) : null);
        }

        public struct AmbientScope : System.IDisposable
        {
            private readonly TransferPresentation previous;

            internal AmbientScope(TransferPresentation presentation)
            {
                previous = ambient;
                ambient = presentation;
            }

            public void Dispose()
            {
                ambient = previous;
            }
        }

        /// <summary>圖層的演出（沒掛就是 null，文字照 DMSE 原樣）。</summary>
        private static TransferPresentation Presentation(PlanetLayerDef layer)
        {
            return layer != null ? TransferEnvironment.For(layer)?.presentation : null;
        }
    }
}
