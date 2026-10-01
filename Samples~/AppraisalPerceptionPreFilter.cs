using System;
using UnityEngine;

namespace AINPCCoreEngine
{
    /// <summary>
    /// AppraisalのAPI呼び出し前に走らせる、距離・任意の視線チェックによる知覚事前フィルタ。
    ///
    /// GossipNet.Runtimeは意図的にTransform/Physicsを扱わない（middlewareは空間について
    /// 何も知らない、という設計境界）ため、これは参考実装としてSamples層に置く
    /// （NpcStateWatcherに対するSampleNpcStateLightと同じ立ち位置）。
    ///
    /// 視線チェックは関数として注入する方式にし、3D(Physics.Linecast)・2D(Physics2D.Linecast)・
    /// 視線チェック無しのいずれの利用側にも対応できるようにしている。
    /// </summary>
    public static class AppraisalPerceptionPreFilter
    {
        /// <summary>
        /// listenerPositionからeventPositionまでの距離がmaxDistanceMeters以内かどうかを判定する。
        /// requireLineOfSightがtrueかつlineOfSightCheckが指定されている場合、それがfalseを返せば
        /// 知覚不可とみなす（lineOfSightCheckを省略した場合は距離のみで判定する）。
        /// </summary>
        public static bool CanPerceive(
            Vector3 listenerPosition,
            Vector3 eventPosition,
            float maxDistanceMeters,
            bool requireLineOfSight,
            Func<Vector3, Vector3, bool> lineOfSightCheck,
            out float distanceMeters)
        {
            distanceMeters = Vector3.Distance(listenerPosition, eventPosition);
            if (distanceMeters > maxDistanceMeters)
                return false;

            if (requireLineOfSight && lineOfSightCheck != null && !lineOfSightCheck(listenerPosition, eventPosition))
                return false;

            return true;
        }
    }
}
