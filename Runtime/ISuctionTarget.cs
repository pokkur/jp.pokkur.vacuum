namespace Vacuum
{
    /// <summary>
    /// LOCAL-0032 「吸い込める対象」の抽象。SuctionSource はこの I/F だけを介して対象を駆動する。
    /// 判定と状態遷移/エフェクトは <see cref="TryBeginAbsorb"/> に一本化し、純粋クエリに副作用を持たせない
    /// （外部から述語だけ呼んで Failed FX が漏れるのを防ぐ）。実装は <see cref="SuctionAbsorbable"/>。
    /// </summary>
    public interface ISuctionTarget
    {
        /// <summary>
        /// 吸えるなら commit して開始状態へ遷移し true。吸えなければ Failed を発火して false。
        /// 旧シグネチャ（motion profile なし）。実装は <see cref="TryBeginAbsorb(in SuctionContext, SuctionMotionProfile)"/>
        /// に <c>null</c> を渡して委譲し、対象側の既定プロファイルで吸引する。
        /// </summary>
        bool TryBeginAbsorb(in SuctionContext ctx);

        /// <summary>
        /// 吸えるなら commit して開始状態へ遷移し true。吸えなければ Failed を発火して false。
        /// LOCAL-0034: ノズル側（<see cref="SuctionSource"/>）の <see cref="SuctionMotionProfile"/> を渡し、
        /// 銃口 1 箇所のスタイル設定を全対象へ効かせる。<paramref name="profile"/> が <c>null</c> なら
        /// 対象側の既定プロファイルを使う。<see cref="SuctionContext"/> は無改修（call-site 波及を避けるため）。
        /// </summary>
        bool TryBeginAbsorb(in SuctionContext ctx, SuctionMotionProfile profile);

        /// <summary>吸引中の中断要求。Starting/Absorbing 中なら逆流（元位置へ戻す）へ遷移する。それ以外は無視。</summary>
        void Interrupt();

        /// <summary>Starting/Absorbing/Backflow のいずれか（再 commit 不可）の間 true。</summary>
        bool IsBusy { get; }
    }
}
