# RadarUtility 快速?考指南 (Quick Reference Guide)

## ?? 核心方法速查 (Core Methods Quick Reference)

### 1?? CalculateRadarCrossSection(float h, float w)
**用途**: ?算??雷?的有效覆?面?
```csharp
float coverage = RadarUtility.CalculateRadarCrossSection(10f, 5f);
// h=10 (有效面?系?), w=5 (覆??度)
```
**??范?**: h > 0, w ? 0  
**返回**: 覆???值  
**集成位置**: CompRadar

---

### 2?? CalculateArrivalTime(float totalDistance, float currentDistance, float velocity)
**用途**: ?算??到?目?的??
```csharp
float time = RadarUtility.CalculateArrivalTime(100f, 0f, 20f);
// totalDistance=100, currentDistance=0, velocity=20
// 返回: 5秒
```
**??范?**: velocity > 0  
**返回**: 到???（秒）  
**集成位置**: SkyfallerTurretComp

---

### 3?? CalculateTotalCoverage(...)
**用途**: ?算多?雷?的?合覆?
```csharp
float[] coverage = new float[] { 8f, 7f, 9f };
float[] distances = new float[] { 10f, 15f, 5f };
float total = RadarUtility.CalculateTotalCoverage(
    10f,           // ?前??
    coverage,      // 覆??度??
    distances,     // 距离??
    20f,           // ??速度
    10f            // 有效面?系?
);
```
**返回**: ??面?值

---

### 4?? CalculateCriticalInterceptionTime(...)
**用途**: ?算?截必?完成的?界??
```csharp
float critical = RadarUtility.CalculateCriticalInterceptionTime(
    radarCoverage,     // 覆??度??
    distances,         // 距离??
    20f,              // ??速度
    10f,              // 有效面?系?
    0.5f              // ?截?值
);
// 如果 timeRemaining < critical，必???最后?刺
```
**返回**: ????（秒）

---

### 5?? CalculateInterceptionWindow(float totalDistance, float velocity, float criticalTime)
**用途**: ?算可用的?截??窗口
```csharp
float window = RadarUtility.CalculateInterceptionWindow(
    100f,          // ?距离
    20f,           // ??速度
    3f             // ????
);
// window = 100/20 - 3 = 2秒
```
**返回**: 可用?截??（秒）

---

### 6?? CalculateProjectileCoverage(...)
**用途**: ?算????的覆???
```csharp
float coverage = RadarUtility.CalculateProjectileCoverage(
    10f,           // ?前??
    5f,            // ??到???
    8f,            // 覆??度
    10f            // 有效面?系?
);
// coverage = (10-5) * G(10,8)
```
**返回**: 覆???值

---

### 7?? CalculateTotalProjectileCoverage(...)
**用途**: ?算多???的?合覆?
```csharp
float[] arrivalTimes = new float[] { 2f, 3f, 4f };
float[] coverageValues = new float[] { 8f, 9f, 7f };
float total = RadarUtility.CalculateTotalProjectileCoverage(
    5f,              // ?前??
    arrivalTimes,    // 到?????
    coverageValues,  // 覆?值??
    10f              // 有效面?系?
);
```
**返回**: ?覆?值

---

## ?? 系?接口速查 (System Interface Quick Reference)

### CompRadar 接口
```csharp
// ?取目?覆?值
float coverage = radar.GetTargetCoverage(target);

// 目???列表
List<WorldObject_Transfer> targets = radar.DetectedTargets;

// 目??定列表
List<WorldObject_Transfer> locked = radar.LockedTargets;

// ?定/解?目?
radar.LockTarget(target);
radar.UnlockTarget(target);

// ?查范?
bool inRange = radar.IsTargetInAimRange(target);
```

### MapComponent_InterceptSkyfaller 接口
```csharp
// ?取炮塔集合
HashSet<SkyfallerTurretComp> turrets = component.turrets;

// ?取空投?据
List<DroppodData> pods = component.Pods;

// ????pod
DroppodData pod = component.Pods[0];
int spawnTime = pod.tickToSpawn;
```

### SkyfallerTurretComp 接口
```csharp
// ?算?截窗口
float window = turret.CalculateFireWindow(remainingTime);

// ?算覆???
float coverage = turret.CalculateCoverageContribution(distance);

// ???性
float velocity = turret.Props.guidanceVelocity;
float threshold = turret.Props.interceptionThreshold;
```

---

## ?? 常用??值 (Common Parameter Values)

### 推荐???合
```
// ???度
antiStealthLevel = 3
irradiationPower = 6
guidanceVelocity = 25
effectiveCrossSection = 12
interceptionThreshold = 0.3

// 中等?度
antiStealthLevel = 2
irradiationPower = 5
guidanceVelocity = 20
effectiveCrossSection = 10
interceptionThreshold = 0.5

// 困??度
antiStealthLevel = 1
irradiationPower = 4
guidanceVelocity = 15
effectiveCrossSection = 8
interceptionThreshold = 0.7

// 极??度
antiStealthLevel = 0
irradiationPower = 3
guidanceVelocity = 12
effectiveCrossSection = 6
interceptionThreshold = 0.85
```

---

## ?? 常?用法模式 (Common Usage Patterns)

### 模式1: 目???与?定
```csharp
foreach (var target in radar.DetectedTargets)
{
    float coverage = radar.GetTargetCoverage(target);
    if (coverage > 5f && radar.LockedTargets.Count < maxLock)
    {
        radar.LockTarget(target);
    }
}
```

### 模式2: ?截?策
```csharp
int timeRemaining = pod.tickToSpawn - Find.TickManager.TicksGame;
float criticalTime = CalculateCriticalInterceptionTime(pod);

if (timeRemaining <= criticalTime && fireReady)
{
    LaunchFinalInterceptor();
}
```

### 模式3: 覆??估
```csharp
float totalCoverage = 0f;
foreach (var missile in activeMissiles)
{
    totalCoverage += RadarUtility.CalculateProjectileCoverage(
        currentTime,
        missile.ArrivalTime,
        missile.CoverageStrength,
        effectiveCrossSection
    );
}

if (totalCoverage >= requiredThreshold)
{
    MarkInterceptionSuccessful();
}
```

### 模式4: ???度?整
```csharp
float difficultyMult = GetDifficultySetting(); // 0.5 ~ 2.0
float adjustedThreshold = baseThreshold * difficultyMult;
float window = CalculateInterceptionWindow(...);

if (window * difficultyMult < minimumWindow)
{
    // ?截窗口不足，提前?射
    LaunchInterceptor();
}
```

---

## ?? ??影?表 (Parameter Impact Table)

| ?? | 增加效果 | ?少效果 |
|------|---------|---------|
| antiStealthLevel | ??距离增加 | ??距离?少 |
| irradiationPower | 覆??度增加 | 覆??度?少 |
| guidanceVelocity | ?截窗口?短 | ?截窗口延? |
| effectiveCrossSection | 覆???增加 | 覆????少 |
| interceptionThreshold | ?度增加 | ?度?少 |

---

## ?? ??技巧 (Debugging Tips)

### 打印覆?值
```csharp
if (Prefs.DevMode)
{
    float coverage = radar.GetTargetCoverage(target);
    Log.Message($"Target coverage: {coverage:F2}");
}
```

### 打印?截??
```csharp
if (Prefs.DevMode)
{
    float critical = CalculateCriticalInterceptionTime(pod);
    int remaining = pod.tickToSpawn - Find.TickManager.TicksGame;
    Log.Message($"Critical: {critical:F1}s, Remaining: {remaining/60f:F1}s");
}
```

### 打印?截窗口
```csharp
if (Prefs.DevMode)
{
    float window = CalculateInterceptionWindow(dist, vel, crit);
    Log.Message($"Interception window: {window:F1}s");
}
```

---

## ?? 性能?控 (Performance Monitoring)

### ?存效率
```csharp
// ?控?存命中率
private int cacheHits = 0;
private int cacheMisses = 0;

if (targetCoverageCache.TryGetValue(target, out float coverage))
{
    cacheHits++;
}
else
{
    cacheMisses++;
}

if (Prefs.DevMode)
{
    Log.Message($"Cache hit rate: {cacheHits / (float)(cacheHits + cacheMisses):P}");
}
```

### ?算性能
```csharp
// ?量?算耗?
System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

float coverage = RadarUtility.CalculateRadarCrossSection(h, w);

sw.Stop();
Log.Message($"Calculation time: {sw.ElapsedMilliseconds}ms");
```

---

## ?? 更多信息 (More Information)

- **??文?**: RadarUtility_Integration_Summary.md
- **使用示例**: RadarUtility_Usage_Examples.md
- **完成清?**: RadarUtility_Integration_Checklist.md
- **完整?告**: RadarUtility_Final_Report.md

---

**最后更新**: 2024年  
**版本**: 1.0  
**????**: ? 成功
