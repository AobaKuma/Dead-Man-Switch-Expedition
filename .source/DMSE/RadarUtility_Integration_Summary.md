# RadarUtility 集成?? (RadarUtility Integration Summary)

## 概述 (Overview)
已成功在 DMSE 模?中集成??制?律?算方法。?些?算基于多重雷??截面?模型的推?公式，用于优化防空??系?的?截成功率。

## ??的?算方法 (Implemented Calculation Methods)

### 1. RadarUtility.cs
包含7????算方法：

#### CalculateRadarCrossSection(float h, float w)
- **功能**: ?算??雷????的有效面???
- **公式**: `G(h, w_i) = h * ((1 + (h - w_i) / h) / 2 + w_i * ((1 - (h - w_i) / h) / 2))`
- **??**:
  - `h`: ???面?系? (有效面?系?)
  - `w`: 第i?雷?的?面??度 (雷?覆??度)

#### CalculateArrivalTime(float totalDistance, float currentDistance, float velocity)
- **功能**: ?算??到?目?的??
- **公式**: `t_i = (L - d_i) / v`
- **??**:
  - `totalDistance`: ??与目?的?距离
  - `currentDistance`: ?前到目?的距离
  - `velocity`: ??速度

#### CalculateTotalCoverage(float time, float[] radarCoverage, float[] distances, float velocity, float h)
- **功能**: ?算n?雷?的??面?
- **公式**: `A = t * Σ G(h, w_i) - Σ ((L - d_i) / v) * G(h, w_i)`
- **用途**: ?估多雷?系?的?合覆?效能

#### CalculateCriticalInterceptionTime(...)
- **功能**: ?算?截成功所需的????
- **公式**: `t_g = Σ ((L - d_i) / v) * G(h, w_i) / (Σ G(h, w_i) - vht)`
- **用途**: 确定最后N秒?必?完成?截的???值

#### CalculateInterceptionWindow(float totalDistance, float velocity, float criticalTime)
- **功能**: ?算可用的?截窗口??
- **公式**: `t_w = L/v - t_g`
- **用途**: 确定可以?火的??范?

#### CalculateProjectileCoverage(float time, float arrivalTime, float radarCoverage, float h)
- **功能**: ?算?????目?的?面???
- **公式**: `A_i = (t - t_i) * G(h, w_i)`

#### CalculateTotalProjectileCoverage(...)
- **功能**: ?算n???的??面?
- **用途**: ?估多??系?的?合覆?

## 集成位置 (Integration Points)

### 1. CompRadar.cs (雷??件)
```csharp
// 新增方法
- CalculateTargetCoverage(WorldObject_Transfer target)
  // 使用 RadarUtility.CalculateRadarCrossSection() ?算目?覆?值
  
- GetTargetCoverage(WorldObject_Transfer target)
  // ??存或???算?取目?覆?值
```

**功能**: 
- ?算雷??各目?的覆??度
- 基于距离和反?形等??行?整
- 支持目??定和可?化

### 2. MapComponent_InterceptSkyfaller.cs (?截管理器)
```csharp
// 新增方法
- CalculateCriticalInterceptionTime(DroppodData pod)
  // 使用 RadarUtility.CalculateCriticalInterceptionTime() 
  // ?算特定目?的?截?界??
```

**功能**:
- ?算每?目?的?截????
- 基于多炮塔的雷?覆??度
- 用于?定最后?刻的?刺?截

### 3. SkyfallerTurretComp.cs (炮塔???件)
```csharp
// 新增方法
- CalculateFireWindow(float remainingTime)
  // 使用 RadarUtility.CalculateInterceptionWindow()
  // ?算可?火的??窗口
  
- CalculateCoverageContribution(float distance)
  // 使用 RadarUtility.CalculateRadarCrossSection()
  // ?算?炮塔的覆???
```

**功能**:
- ?算各炮塔的火力控制??
- 基于??速度和有效面?系?
- 支持自适?的?截?策

## ??配置 (Parameter Configuration)

### CompRadar ??
```
searchTileRadius = 10        // 搜索半?
aimTileRadius = 10           // 瞄准半?
antiStealthLevel = 0         // 反?形等?
irradiationPower = 5         // 照射?度
maxTargetLock = 3            // 最大?定目??
```

### SkyfallerTurretComp ??
```
guidanceVelocity = 20f       // ??速度
effectiveCrossSection = 10f  // 有效面?系?
interceptionThreshold = 0.5f // ?截?值
```

## 工作流程 (Workflow)

1. **搜索?段 (Search Phase)**
   - CompRadar ??范??的目?
   - 使用 CalculateRadarCrossSection() ?算覆?值
   - 保存到?存用于后?查?

2. **追??段 (Tracking Phase)**
   - MapComponent_InterceptSkyfaller ?控目?到???
   - ??更新目?位置和距离信息

3. **?截?段 (Interception Phase)**
   - ?目??入?截??窗口?触?
   - CalculateCriticalInterceptionTime() 确定最后?截机?
   - SkyfallerTurretComp 根据 CalculateFireWindow() ?定是否?火

4. **?估?段 (Evaluation Phase)**
   - 基于多炮塔的?合覆?效能?估
   - ???整?截策略

## ??基? (Mathematical Foundation)

所有?算基于以下假?：
- ??以恒定速度直??行
- 雷?覆??度?距离?性衰?
- 多雷?的覆?效果?加
- ?截成功需要?足最小覆??值

## ?展建? (Extension Suggestions)

1. 可根据??游?需求?整各??
2. 可添加?机性以增加游??度
3. 可考?目??避机制
4. 可集成更复?的多目?优先?算法

---
**集成日期**: 2024年
**????**: ? 成功 (Build Success)
