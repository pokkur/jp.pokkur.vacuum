# Vacuum

Vacuum は、Unity 向けの依存なし UPM パッケージです。メッシュ対象へ `SuctionAbsorbable` を追加し、ノズル側の `SuctionSource` から駆動することで、5 状態ライフサイクル付きのスタイライズされた吸引表現を実現します。

![ノズルがメッシュ対象を吸い込む Vacuum デモ](media/vacuum-demo.gif)

## 要件

- Unity `6000.4` 以降
- `UnityEngine` 以外の依存なし

## パッケージ内容

| Path | 内容 |
| --- | --- |
| `Runtime/` | `SuctionSource`、`SuctionAbsorbable`、`SuctionContext`、`SuctionMotionProfile`、`ISuctionTarget` などの Runtime API。 |
| `Samples~/BasicVacuum/` | ノズル、複数の対象、簡易入力ドライバを含む import 可能なサンプル。 |
| `Tests/Editor/` | source lifecycle、target state、acquisition、category、interruption を検証する EditMode tests。 |
| `Documentation~/media/vacuum-demo.gif` | 吸引変形を確認できる軽量なアニメーションキャプチャ。 |
| `README.md` | 英語 README。 |
| `README.ja.md` | 日本語 README。 |
| `llms.txt` | AI エージェント向けの高シグナルなドキュメント/ソース入口。 |
| `CHANGELOG.md` | 変更履歴。 |
| `LICENSE.md` | MIT license。 |

## 基本手順

1. `MeshFilter` を持つメッシュ対象に `SuctionAbsorbable` を追加します。
2. `SuctionSource` から取得したい対象には `Collider` も付けます。
3. ノズル GameObject に `SuctionSource` を追加します。
4. 照準直撃型なら `Raycast`、範囲吸引なら `Volume` を選びます。
5. コントローラ側で毎フレーム `SuctionContext` を作り、`SuctionSource.Tick(ctx)` を呼びます。
6. ノズル側の `MotionProfile` で速度、曲線/螺旋、渦、伸縮、スナップ、震えを調整します。

## 最小ドライバ

```csharp
using UnityEngine;
using Vacuum;

[RequireComponent(typeof(SuctionSource))]
public sealed class MyNozzle : MonoBehaviour
{
    [SerializeField] Camera aimCamera;
    [SerializeField] float power = 1f;
    [SerializeField] float maxDistance = 30f;

    SuctionSource source;

    void Awake()
    {
        source = GetComponent<SuctionSource>();
    }

    void Update()
    {
        bool held = Input.GetMouseButton(0);
        Ray aim = aimCamera.ScreenPointToRay(Input.mousePosition);

        var ctx = new SuctionContext(
            transform,
            transform.position,
            aim,
            power,
            maxDistance,
            held);

        source.Tick(ctx);
    }
}
```

## サンプル

Package Manager の Samples tab から **Basic Vacuum Demo** を import できます。Play Mode で左クリックを押しながら対象を狙うと吸引し、吸引中に離すと `Backflow` による中断復帰を確認できます。

詳細は `README.ja.md` を参照してください。
