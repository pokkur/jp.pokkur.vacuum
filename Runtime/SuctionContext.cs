using UnityEngine;

namespace Vacuum
{
    /// <summary>
    /// LOCAL-0032 吸引の 1 フレーム分の入力をまとめた値オブジェクト。呼び出し側（ノズル側ドライバ）が
    /// 毎フレーム組んで <see cref="ISuctionTarget.TryBeginAbsorb"/> / SuctionSource へ渡す。純粋な入力なので
    /// readonly struct とし、<c>in</c> 引数で防御的コピーを避ける。
    /// </summary>
    public readonly struct SuctionContext
    {
        /// <summary>吸い込み先のノズル Transform。null 許容。非 null なら吸引中も毎フレーム位置を読んで追従する。</summary>
        public readonly Transform Nozzle;

        /// <summary>commit 時点のノズル world 位置。<see cref="Nozzle"/> が null のときの収束先フォールバック。</summary>
        public readonly Vector3 NozzlePosition;

        /// <summary>generic 吸引専用の照準 ray（泡 launch ray とは別物）。source 側の raycast / aim-leave 判定に使う。</summary>
        public readonly Ray AimRay;

        /// <summary>emitter で計算済みの吸引パワー（入り抜き込み）。対象の必要パワー閾値と突き合わせる。</summary>
        public readonly float Power;

        /// <summary>ノズルまでの最大射程 (m)。0 で無制限。</summary>
        public readonly float MaxDistance;

        /// <summary>トリガ保持中か。離した瞬間に吸収中対象を逆流させる（中断＝逆流）判定に使う。</summary>
        public readonly bool Held;

        public SuctionContext(Transform nozzle, Vector3 nozzlePosition, Ray aimRay, float power, float maxDistance, bool held)
        {
            Nozzle = nozzle;
            NozzlePosition = nozzlePosition;
            AimRay = aimRay;
            Power = power;
            MaxDistance = maxDistance;
            Held = held;
        }

        /// <summary>
        /// 収束先の world 位置を解決する。<see cref="Nozzle"/> が生きていればその現在位置、無ければ <see cref="NozzlePosition"/>。
        /// </summary>
        public Vector3 ResolveNozzlePosition()
        {
            return Nozzle != null ? Nozzle.position : NozzlePosition;
        }
    }
}
