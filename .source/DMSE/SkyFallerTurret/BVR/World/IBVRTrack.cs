using Fortified;

namespace DMSE
{
    /// <summary>
    /// 防空網路內部看到的航跡：<see cref="IAirTrack"/> 再加上網路自己需要的資料。
    /// 外部 mod 不需要實作這個介面，只實作 <see cref="IAirTrack"/>，網路會用 <see cref="BVRExternalTrack"/> 包裝。
    /// </summary>
    public interface IBVRTrack : IAirTrack
    {
        BVRTargetProps TargetProps { get; }

        /// <summary>裝配系統的飛彈設定；非飛彈為 null。</summary>
        MissileConfig Config { get; }

        BVRTrackState State { get; }

        AirTrackSignature IAirTrack.Signature => TargetProps.Signature;
    }
}
