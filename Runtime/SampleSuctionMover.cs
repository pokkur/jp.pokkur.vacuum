using UnityEngine;

namespace Vacuum
{
    /// <summary>
    /// LOCAL-0032 サンプル専用の簡易ムーバ。軸方向の往復（横/上下）＋任意のスピンで「動いている対象」を作り、
    /// 移動中でも狙って吸えることを確認する。sibling の <see cref="SuctionAbsorbable"/> が吸引中（IsBusy）の間は
    /// 停止する — commit 後に Transform が動くと funnel warp の基準がズレるため。位相は移動中だけ進めるので、
    /// 逆流で Idle に戻ったとき掴まれた位置から滑らかに往復を再開する。
    /// </summary>
    public sealed class SampleSuctionMover : MonoBehaviour
    {
        [Tooltip("往復する方向（ローカル）。右(1,0,0)で横往復、上(0,1,0)で上下バウンド。長さは正規化される。")]
        [SerializeField]
        Vector3 oscillateAxis = Vector3.right;

        [Tooltip("往復の振幅 (m)。0 で往復しない（スピンのみ等）。")]
        [SerializeField]
        [Min(0f)]
        float oscillateAmplitude = 1.5f;

        [Tooltip("往復の速さ（往復/秒に比例）。0 で停止。")]
        [SerializeField]
        [Min(0f)]
        float oscillateSpeed = 0.5f;

        [Tooltip("スピン角速度（度/秒・ローカル各軸）。0 で回転しない。")]
        [SerializeField]
        Vector3 spinDegreesPerSecond = Vector3.zero;

        SuctionAbsorbable absorbable;
        Vector3 home;
        float phase;
        bool initialized;

        void Awake()
        {
            absorbable = GetComponent<SuctionAbsorbable>();
            home = transform.localPosition;
            initialized = true;
        }

        void Update()
        {
            if (!initialized)
            {
                return;
            }

            // 吸引中（commit〜逆流）は止める。Transform が動くと funnel warp 基準がズレるため。
            // 位相を進めず位置も書かないので、逆流で Idle に戻ったとき掴まれた位置から連続再開する。
            if (absorbable != null && absorbable.IsBusy)
            {
                return;
            }

            float deltaTime = Time.deltaTime;

            if (oscillateAmplitude > 0f && oscillateSpeed > 0f && oscillateAxis.sqrMagnitude > 1e-6f)
            {
                phase += deltaTime * oscillateSpeed;
                Vector3 offset = oscillateAxis.normalized * (Mathf.Sin(phase * 2f * Mathf.PI) * oscillateAmplitude);
                transform.localPosition = home + offset;
            }

            if (spinDegreesPerSecond.sqrMagnitude > 1e-6f)
            {
                transform.Rotate(spinDegreesPerSecond * deltaTime, Space.Self);
            }
        }
    }
}
