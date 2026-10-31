# 長程巡飛彈：資料依據與遊戲平衡

以 Shahed-136 為原型的長程巡飛彈（`DMSE_Missile_Shahed`），製造需要 `DMSE_LoiteringMunitions` 與原版 `MicroelectronicsBasics`。不另開科技，也不要求模組後期的積體電路科技。

## 公開資料依據

- [ARES / Open Source Munitions Portal：Shahed-131 & -136 UAVs: a visual guide](https://osmp.ngo/collection/shahed-131-136-uavs-a-visual-guide/)：實物辨識資料記載原型約 50 kg 戰鬥部、活塞引擎、約 2,000 km 等級航程，以及飛往預設座標的 GNSS 導航。後期俄製 90 kg 戰鬥部並非這次採用的原型。
- [RUSI：Russia’s Iranian-Made UAVs: A Technical Profile](https://www.rusi.org/explore-our-research/publications/commentary/russias-iranian-made-uavs-technical-profile)：低成本商用電子與活塞動力、遠程固定目標用途；航程估計有差異，約 1,000–2,000 km。採用其角色定位，不把估計值當成精確規格。
- [Janes：IRGC confirms specs for Shahed-136 attack UAV](https://www.janes.com/defence-intelligence-insights/defence-news/irgc-confirms-specs-for-shahed-136-attack-uav)：報導公開展板的 200 kg 全重、50 kg 戰鬥部與 185 km/h 速度。本次使用基本螺旋槳型，非噴射動力的 Shahed-238。

## 遊戲數值

以下為模組內的平衡換算，非真實武器效能模型。世界格、傷害與 BVR 速度均使用既有遊戲單位。

| 數值 | V1 老式巡飛彈 | 反艦巡飛彈 | Shahed-136 |
|---|---:|---:|---:|
| 射程（世界格） | 12 | 17 | 60 |
| 世界速度係數 | 0.75 | 0.65 | 0.25 |
| BVR 速度 | 20 | 15 | 7 |
| 基礎傷害 | 90 | 70 | 55 |
| 爆炸半徑 | 4 | 3 | 2.5 |
| 額外落點散布 | 6 | 4 | 2 |
| 終端追蹤 | 無 | 有 | 無 |

射程為 `50 × 12 × 0.1 = 60`：這裡的比衝欄位是遊戲既有的航程效率係數，以較高值表現活塞螺旋槳的續航，並非宣稱活塞引擎有某個真實火箭比衝。較低的速度與單發威力，交換早期可用的遠程廉價打擊能力。

雷達距離等級 0.8、隱身等級 0、航跡高度 300：以較小目標與低空縮短預警，但不給予匿蹤能力。BVR 速度低於 V1，仍容易被攔截。高度與距離等級為遊戲平衡參數。

## 接線與限制

- 預設慣性制導，只有一個導引槽；可在物品裝配頁消耗 **1 個高級零件**選配電視制導。僅能傾斜發射，可用於 launch rail、rotary launch rail 與 loitering munition rack。
- 預設 `GuidanceType_Inertial`，採地圖中心圓形正態選點；選配 `GuidanceType_TV` 才在抵達時開放玩家手動選點。電視模組是遊戲的選配升級，不代表基本型 Shahed-136 實機具有電視尋標器。敵方電視制導仍使用慣性後備選點。
- 通道成本依導引方式（`StrikeChannelCost`）：慣性與電視手選都是 0 個持續火控通道（2026-10 科技重整後；原本為 1／2）。
- `terminalUpdates=0`，不追蹤移動據點；預設慣性不能跨圖層，選配電視模組具備既有電視制導的跨圖層能力。
- 物品使用 `Shahed/ammo`，升空與來襲使用 `Shahed/skyFaller_incoming`；火箭助推離軌特效沿用巡飛彈模板。
- 單枚材料：化學燃料 50、鋼鐵 60、工業零件 10（2026-10-27 依 T0 材料分代改寫，市值不變）。電視升級另付 1 個高級零件；質量 200、耐久 50。
- 耐久衰減 2 HP/日，存入巡飛彈儲存架或兩種相容發射架可停止衰減。

## 顯示名稱與助推效果

遊戲內通稱「長程巡飛彈」（英文 long-range loitering munition；簡中 远程巡飞弹），保留既有 Def ID 與貼圖路徑。發射助推火焰與煙霧限制為 30 ticks（約 0.5 秒），尺寸為原來的 0.35 倍；助推結束後停止新增尾焰與煙霧，彈體繼續飛離地圖。其餘飛彈預設維持全程噴焰與原尺寸。
