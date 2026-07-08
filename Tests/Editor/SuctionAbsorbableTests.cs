using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Vacuum;

namespace Vacuum.Tests
{
    [TestFixture]
    public sealed class SuctionAbsorbableTests
    {
        const float k_largeStep = 10f;

        GameObject targetObject;
        GameObject nozzleObject;
        SuctionAbsorbable absorbable;
        Mesh testMesh;

        int startedCount;
        int absorbingCount;
        int failedCount;
        int backflowCount;
        int completedCount;

        [SetUp]
        public void SetUp()
        {
            nozzleObject = new GameObject("TestNozzle");
            nozzleObject.transform.position = new Vector3(0f, 0f, 2f);

            targetObject = new GameObject("TestAbsorbable");
            targetObject.transform.position = Vector3.zero;

            var meshFilter = targetObject.AddComponent<MeshFilter>();
            testMesh = BuildQuadMesh();
            meshFilter.sharedMesh = testMesh;

            absorbable = targetObject.AddComponent<SuctionAbsorbable>();
            // 検証を繰り返せるよう完了で破棄しない（EditMode で Destroy はエラー扱いになるため）。
            ConfigureForTest(absorbable);

            startedCount = 0;
            absorbingCount = 0;
            failedCount = 0;
            backflowCount = 0;
            completedCount = 0;
            absorbable.OnAbsorbStarted.AddListener(() => startedCount++);
            absorbable.OnAbsorbing.AddListener(() => absorbingCount++);
            absorbable.OnAbsorbFailed.AddListener(() => failedCount++);
            absorbable.OnAbsorbBackflow.AddListener(() => backflowCount++);
            absorbable.OnAbsorbCompleted.AddListener(() => completedCount++);
        }

        [TearDown]
        public void TearDown()
        {
            if (targetObject != null)
            {
                Object.DestroyImmediate(targetObject);
                targetObject = null;
            }

            if (nozzleObject != null)
            {
                Object.DestroyImmediate(nozzleObject);
                nozzleObject = null;
            }

            if (testMesh != null)
            {
                Object.DestroyImmediate(testMesh);
                testMesh = null;
            }
        }

        [Test]
        public void BelowThreshold_DoesNotCommit_StaysIdle_AndFiresFailedOnce()
        {
            SuctionContext ctx = MakeContext(power: 0.1f, held: true);

            bool committed = absorbable.TryBeginAbsorb(ctx);

            Assert.That(committed, Is.False);
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Idle));
            Assert.That(absorbable.IsBusy, Is.False);
            Assert.That(failedCount, Is.EqualTo(1));
            Assert.That(startedCount, Is.EqualTo(0));
            // 変形しない: メッシュは差し替えられず元の共有メッシュのまま。
            Assert.That(targetObject.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(testMesh));
        }

        [Test]
        public void Commit_AdvancesThroughStartingAbsorbingToCompleted_FiringEachEventOnce()
        {
            SuctionContext ctx = MakeContext(power: 1f, held: true);

            bool committed = absorbable.TryBeginAbsorb(ctx);

            Assert.That(committed, Is.True);
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Starting));
            Assert.That(startedCount, Is.EqualTo(1));
            Assert.That(absorbable.IsBusy, Is.True);

            absorbable.TickForTest(k_largeStep); // windup を抜けて Absorbing へ
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Absorbing));
            Assert.That(absorbingCount, Is.EqualTo(1));

            absorbable.TickForTest(k_largeStep); // effectiveDuration を超えて Completed へ
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Completed));
            Assert.That(completedCount, Is.EqualTo(1));
            Assert.That(absorbable.IsBusy, Is.False);

            // 中間で止まらず一度ずつだけ解決する。逆流・失敗は発火しない。
            Assert.That(backflowCount, Is.EqualTo(0));
            Assert.That(failedCount, Is.EqualTo(0));
        }

        [Test]
        public void InterruptDuringAbsorbing_BacksFlowToIdle_AndRestoresPhysics()
        {
            var collider = targetObject.AddComponent<BoxCollider>();
            SuctionContext ctx = MakeContext(power: 1f, held: true);

            Assert.That(absorbable.TryBeginAbsorb(ctx), Is.True);
            absorbable.TickForTest(k_largeStep); // → Absorbing
            absorbable.TickForTest(0.01f);       // 少しだけ吸引を進める（完了はしない）
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Absorbing));
            Assert.That(collider.enabled, Is.False, "commit で collider が無効化される");

            absorbable.Interrupt();
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Backflow));
            Assert.That(backflowCount, Is.EqualTo(1));
            Assert.That(absorbable.IsBusy, Is.True);

            absorbable.TickForTest(k_largeStep); // 逆流完走 → Idle
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Idle));
            Assert.That(absorbable.IsBusy, Is.False);
            Assert.That(collider.enabled, Is.True, "逆流復帰で collider が再有効化される");

            // 逆流で解決し、完了は発火しない。
            Assert.That(completedCount, Is.EqualTo(0));
        }

        [Test]
        public void Interrupt_WhenIdle_IsIgnored()
        {
            absorbable.Interrupt();

            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Idle));
            Assert.That(backflowCount, Is.EqualTo(0));
        }

        [Test]
        public void NotSuckable_RejectsCommit_StaysIdle_AndFiresFailed()
        {
            absorbable.Suckable = false;
            SuctionContext ctx = MakeContext(power: 1f, held: true);

            bool committed = absorbable.TryBeginAbsorb(ctx);

            Assert.That(committed, Is.False);
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Idle));
            Assert.That(failedCount, Is.EqualTo(1));
            // 変形しない: メッシュは差し替えられず元の共有メッシュのまま。
            Assert.That(targetObject.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(testMesh));
        }

        [Test]
        public void Progress_AdvancesFromZeroToOne()
        {
            Assert.That(absorbable.Progress, Is.EqualTo(0f), "commit 前は 0");

            SuctionContext ctx = MakeContext(power: 1f, held: true);
            Assert.That(absorbable.TryBeginAbsorb(ctx), Is.True);
            Assert.That(absorbable.Progress, Is.EqualTo(0f), "Starting 中（elapsed=0）は 0");

            absorbable.TickForTest(k_largeStep); // windup を抜けて Absorbing（elapsed=0）へ
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Absorbing));

            absorbable.TickForTest(0.05f); // 吸引途中（effectiveDuration の途中）
            Assert.That(absorbable.Progress, Is.GreaterThan(0f));
            Assert.That(absorbable.Progress, Is.LessThan(1f));

            absorbable.TickForTest(k_largeStep); // effectiveDuration を超えて Completed へ
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Completed));
            Assert.That(absorbable.Progress, Is.EqualTo(1f), "完了で 1");
        }

        [Test]
        public void MinimumEffectDuration_KeepsSmallTargetAbsorbingForConfiguredTime()
        {
            absorbable.MinimumEffectDuration = 1f;
            Assert.That(absorbable.TryBeginAbsorb(MakeContext(power: 1f, held: true)), Is.True);

            absorbable.TickForTest(k_largeStep); // windup を抜ける。余剰時間は吸引進捗へ持ち越さない。
            absorbable.TickForTest(0.5f);

            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Absorbing));
            Assert.That(absorbable.Progress, Is.EqualTo(0.5f).Within(0.001f));

            absorbable.TickForTest(0.51f);
            Assert.That(absorbable.State, Is.EqualTo(SuctionAbsorbable.AbsorbState.Completed));
        }

        [Test]
        public void SuctionResistance_AliasesRequiredSuctionPower()
        {
            absorbable.SuctionResistance = 1.5f;
            Assert.That(absorbable.RequiredSuctionPower, Is.EqualTo(1.5f));

            absorbable.RequiredSuctionPower = 0.3f;
            Assert.That(absorbable.SuctionResistance, Is.EqualTo(0.3f));
        }

        SuctionContext MakeContext(float power, bool held)
        {
            Vector3 nozzlePos = nozzleObject.transform.position;
            var aim = new Ray(nozzlePos, targetObject.transform.position - nozzlePos);
            return new SuctionContext(nozzleObject.transform, nozzlePos, aim, power, 0f, held);
        }

        static void ConfigureForTest(SuctionAbsorbable target)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty("requiredSuctionPower").floatValue = 0.5f;
            serialized.FindProperty("destroyOnComplete").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "SuctionTestQuad" };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
