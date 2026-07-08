using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Vacuum;

namespace Vacuum.Tests
{
    [TestFixture]
    public sealed class SuctionSourceTests
    {
        GameObject sourceObject;
        SuctionSource source;
        readonly System.Collections.Generic.List<GameObject> physicsObjects =
            new System.Collections.Generic.List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            sourceObject = new GameObject("TestSuctionSource");
            source = sourceObject.AddComponent<SuctionSource>();
        }

        [TearDown]
        public void TearDown()
        {
            if (sourceObject != null)
            {
                Object.DestroyImmediate(sourceObject);
                sourceObject = null;
            }

            for (int i = physicsObjects.Count - 1; i >= 0; i--)
            {
                if (physicsObjects[i] != null)
                {
                    Object.DestroyImmediate(physicsObjects[i]);
                }
            }
            physicsObjects.Clear();
        }

        [Test]
        public void CommittedAndBusy_WhileHeld_ConsumesWithoutInterrupting()
        {
            var fake = new FakeSuctionTarget { IsBusy = true };
            source.CommittedForTest = fake;

            bool consumed = source.Tick(MakeContext(held: true));

            Assert.That(consumed, Is.True);
            Assert.That(fake.InterruptCount, Is.EqualTo(0));
            Assert.That(source.CommittedForTest, Is.SameAs(fake), "進行中対象は保持される");
        }

        [Test]
        public void CommittedAndBusy_WhenReleased_InterruptsButKeepsCommittedForBackflow()
        {
            var fake = new FakeSuctionTarget { IsBusy = true };
            source.CommittedForTest = fake;

            bool consumed = source.Tick(MakeContext(held: false));

            Assert.That(consumed, Is.True, "Backflow 中も consumed を返し泡 erase 混入を防ぐ");
            Assert.That(fake.InterruptCount, Is.EqualTo(1));
            Assert.That(source.CommittedForTest, Is.SameAs(fake), "Interrupt 後も逆流完了まで保持");
        }

        [Test]
        public void CommittedButNoLongerBusy_IsReleased_AndDoesNotConsumeWhenIdle()
        {
            // Backflow 完了で Idle に戻った対象（IsBusy=false）。held=false なので raycast 取得もしない。
            var fake = new FakeSuctionTarget { IsBusy = false };
            source.CommittedForTest = fake;

            bool consumed = source.Tick(MakeContext(held: false));

            Assert.That(consumed, Is.False);
            Assert.That(source.CommittedForTest, Is.Null, "Idle に戻った対象は解放される");
        }

        [Test]
        public void NoTarget_NotHeld_DoesNothing()
        {
            bool consumed = source.Tick(MakeContext(held: false));

            Assert.That(consumed, Is.False);
            Assert.That(source.HasCommittedTarget, Is.False);
        }

        [Test]
        public void TryCommitCandidate_PassesSourceMotionProfile_ToTarget()
        {
            var fake = new FakeSuctionTarget { BeginResult = true };

            bool consumed = source.TryCommitCandidate(fake, MakeContext(held: true));

            Assert.That(consumed, Is.True);
            Assert.That(fake.BeginCount, Is.EqualTo(1));
            Assert.That(fake.LastProfile, Is.SameAs(source.MotionProfile), "銃口の MotionProfile が commit 時に対象へ渡る");
            Assert.That(source.CommittedForTest, Is.SameAs(fake));
        }

        [Test]
        public void MaxConcurrentOne_SecondCandidate_GetsNoSlot()
        {
            var first = new FakeSuctionTarget { BeginResult = true };
            var second = new FakeSuctionTarget { BeginResult = true };
            SuctionContext ctx = MakeContext(held: true);

            Assert.That(source.TryCommitCandidate(first, ctx), Is.True);
            Assert.That(source.HasCommittedTarget, Is.True);

            // 既定 maxConcurrent=1。2 体目は枠が無く commit されず TryBeginAbsorb も呼ばれない。
            bool secondConsumed = source.TryCommitCandidate(second, ctx);
            Assert.That(secondConsumed, Is.False);
            Assert.That(second.BeginCount, Is.EqualTo(0));
        }

        [Test]
        public void MaxConcurrentTwo_CommitsTwoTargets()
        {
            SetMaxConcurrent(source, 2);
            var first = new FakeSuctionTarget { BeginResult = true };
            var second = new FakeSuctionTarget { BeginResult = true };
            SuctionContext ctx = MakeContext(held: true);

            Assert.That(source.TryCommitCandidate(first, ctx), Is.True);
            Assert.That(source.TryCommitCandidate(second, ctx), Is.True);

            Assert.That(first.BeginCount, Is.EqualTo(1));
            Assert.That(second.BeginCount, Is.EqualTo(1));
            Assert.That(source.HasCommittedTarget, Is.True);
        }

        [Test]
        public void FailedCandidate_IsDebouncedWhileHeld_AndRetriesAfterRelease()
        {
            var fake = new FakeSuctionTarget { BeginResult = false }; // 吸えない（too strong 相当）
            SuctionContext heldCtx = MakeContext(held: true);

            Assert.That(source.TryCommitCandidate(fake, heldCtx), Is.True, "当たってはいる → consumed");
            Assert.That(fake.BeginCount, Is.EqualTo(1));

            // 保持中は同じ対象に再 Failed しない（再 TryBeginAbsorb しない）。
            Assert.That(source.TryCommitCandidate(fake, heldCtx), Is.True);
            Assert.That(fake.BeginCount, Is.EqualTo(1), "保持中は再試行しない（デバウンス）");

            // トリガ解放でデバウンス解除 → 再試行できる。
            source.Tick(MakeContext(held: false));
            Assert.That(source.TryCommitCandidate(fake, heldCtx), Is.True);
            Assert.That(fake.BeginCount, Is.EqualTo(2));
        }

        [Test]
        public void AdoptCommitted_MakesExternalTargetInterruptibleOnRelease()
        {
            // 外部で commit 済み相当（OverlapSphere では拾えない）の busy 対象を預ける。
            var fake = new FakeSuctionTarget { IsBusy = true };
            source.AdoptCommitted(fake);
            Assert.That(source.HasCommittedTarget, Is.True);

            // 保持中はそのまま consumed・中断しない。
            Assert.That(source.Tick(MakeContext(held: true)), Is.True);
            Assert.That(fake.InterruptCount, Is.EqualTo(0));

            // 解放で中断＝逆流（committed は逆流完了まで保持）。
            Assert.That(source.Tick(MakeContext(held: false)), Is.True);
            Assert.That(fake.InterruptCount, Is.EqualTo(1));
            Assert.That(source.CommittedForTest, Is.SameAs(fake));
        }

        [Test]
        public void AdoptCommitted_IgnoresDuplicateAndNull()
        {
            var fake = new FakeSuctionTarget { IsBusy = true };
            source.AdoptCommitted(fake);
            source.AdoptCommitted(fake); // 二重登録は無視
            source.AdoptCommitted(null); // null は無視

            // 解放で 1 回だけ中断される（重複していれば複数回 Interrupt されてしまう）。
            source.Tick(MakeContext(held: false));
            Assert.That(fake.InterruptCount, Is.EqualTo(1));
        }

        [Test]
        public void Categories_EmptyAllowed_PassesEverything()
        {
            Assert.That(SuctionSource.MatchesCategories(null, null, SuctionFilterMode.Any), Is.True);
            Assert.That(SuctionSource.MatchesCategories(new[] { "enemy" }, new string[0], SuctionFilterMode.All), Is.True);
        }

        [Test]
        public void Categories_AnyMode_MatchesIfTargetHasAnyAllowed()
        {
            string[] allowed = { "enemy", "boss" };
            Assert.That(SuctionSource.MatchesCategories(new[] { "enemy" }, allowed, SuctionFilterMode.Any), Is.True);
            Assert.That(SuctionSource.MatchesCategories(new[] { "debris" }, allowed, SuctionFilterMode.Any), Is.False);
            Assert.That(SuctionSource.MatchesCategories(null, allowed, SuctionFilterMode.Any), Is.False, "カテゴリ無しの対象は Any 不一致");
        }

        [Test]
        public void Categories_AllMode_RequiresEveryAllowed()
        {
            string[] allowed = { "enemy", "flying" };
            Assert.That(SuctionSource.MatchesCategories(new[] { "enemy", "flying" }, allowed, SuctionFilterMode.All), Is.True);
            Assert.That(SuctionSource.MatchesCategories(new[] { "enemy" }, allowed, SuctionFilterMode.All), Is.False, "1 つでも欠ければ All 不一致");
        }

        [Test]
        public void RaycastMode_CommitsOnlyDirectlyHitTarget()
        {
            source.AcquisitionMode = SuctionAcquisitionMode.Raycast;
            TestSuctionTargetComponent direct = CreatePhysicsTarget("Direct", new Vector3(0f, 0f, 5f));
            TestSuctionTargetComponent offAxis = CreatePhysicsTarget("OffAxis", new Vector3(2f, 0f, 5f));
            Physics.SyncTransforms();

            bool consumed = source.Tick(MakeContext(held: true));

            Assert.That(consumed, Is.True);
            Assert.That(direct.BeginCount, Is.EqualTo(1));
            Assert.That(offAxis.BeginCount, Is.EqualTo(0), "ray から外れた対象は取得しない");
        }

        [Test]
        public void RaycastMode_NonTargetColliderBlocksTargetBehindIt()
        {
            source.AcquisitionMode = SuctionAcquisitionMode.Raycast;
            CreatePhysicsObject("Occluder", new Vector3(0f, 0f, 2f), addTarget: false);
            TestSuctionTargetComponent behind = CreatePhysicsTarget("Behind", new Vector3(0f, 0f, 5f));
            Physics.SyncTransforms();

            bool consumed = source.Tick(MakeContext(held: true));

            Assert.That(consumed, Is.False);
            Assert.That(behind.BeginCount, Is.EqualTo(0), "最前面の非対象 collider 越しには取得しない");
        }

        [Test]
        public void RaycastMode_TargetMotionAfterCommit_DoesNotSelfInterrupt()
        {
            source.AcquisitionMode = SuctionAcquisitionMode.Raycast;
            SetAimLeaveAngle(source, 5f);
            TestSuctionTargetComponent target = CreatePhysicsTarget("MovingTarget", new Vector3(0f, 0f, 5f));
            Physics.SyncTransforms();

            source.Tick(MakeContext(held: true));
            target.transform.position = new Vector3(3f, 0f, 3f);
            Physics.SyncTransforms();
            source.Tick(MakeContext(held: true));

            Assert.That(target.InterruptCount, Is.EqualTo(0),
                "吸引アニメーション等で対象自身が ray 外へ移動しても、照準方向を変えていなければ中断しない");
        }

        [Test]
        public void RaycastMode_AimDirectionChange_InterruptsCommittedTarget()
        {
            source.AcquisitionMode = SuctionAcquisitionMode.Raycast;
            SetAimLeaveAngle(source, 5f);
            TestSuctionTargetComponent target = CreatePhysicsTarget("AimLeaveTarget", new Vector3(0f, 0f, 5f));
            Physics.SyncTransforms();

            source.Tick(MakeContext(held: true));
            var changedAim = new Ray(Vector3.zero, new Vector3(1f, 0f, 1f).normalized);
            source.Tick(new SuctionContext(null, Vector3.zero, changedAim, power: 1f, maxDistance: 10f, held: true));

            Assert.That(target.InterruptCount, Is.EqualTo(1));
        }

        static void SetMaxConcurrent(SuctionSource target, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty("maxConcurrent").intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetAimLeaveAngle(SuctionSource target, float value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty("aimLeaveAngle").floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static SuctionContext MakeContext(bool held)
        {
            var aim = new Ray(Vector3.zero, Vector3.forward);
            return new SuctionContext(null, Vector3.zero, aim, power: 1f, maxDistance: 10f, held: held);
        }

        TestSuctionTargetComponent CreatePhysicsTarget(string name, Vector3 position)
        {
            return CreatePhysicsObject(name, position, addTarget: true).GetComponent<TestSuctionTargetComponent>();
        }

        GameObject CreatePhysicsObject(string name, Vector3 position, bool addTarget)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            go.AddComponent<BoxCollider>();
            if (addTarget)
            {
                go.AddComponent<TestSuctionTargetComponent>();
            }
            physicsObjects.Add(go);
            return go;
        }

        // 純 C# のフェイク（UnityEngine.Object でない＝常に生存扱い）。raycast を伴わない committed-lifecycle 検証用。
        sealed class FakeSuctionTarget : ISuctionTarget
        {
            public bool IsBusy { get; set; }
            public int InterruptCount { get; private set; }
            public int BeginCount { get; private set; }
            public bool BeginResult { get; set; }
            public SuctionMotionProfile LastProfile { get; private set; }

            public bool TryBeginAbsorb(in SuctionContext ctx)
            {
                return TryBeginAbsorb(ctx, null);
            }

            public bool TryBeginAbsorb(in SuctionContext ctx, SuctionMotionProfile profile)
            {
                BeginCount++;
                LastProfile = profile;
                IsBusy = BeginResult;
                return BeginResult;
            }

            public void Interrupt()
            {
                InterruptCount++;
            }
        }
    }

    sealed class TestSuctionTargetComponent : MonoBehaviour, ISuctionTarget
    {
        public int BeginCount { get; private set; }
        public int InterruptCount { get; private set; }
        public bool IsBusy { get; private set; }

        public bool TryBeginAbsorb(in SuctionContext ctx)
        {
            return TryBeginAbsorb(ctx, null);
        }

        public bool TryBeginAbsorb(in SuctionContext ctx, SuctionMotionProfile profile)
        {
            BeginCount++;
            IsBusy = true;
            return true;
        }

        public void Interrupt()
        {
            InterruptCount++;
            IsBusy = false;
        }
    }
}
