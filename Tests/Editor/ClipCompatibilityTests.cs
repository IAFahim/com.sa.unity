using NUnit.Framework;
using SA.Editor;
using Unity.Mathematics;

namespace SA.Tests
{
    public class ClipCompatibilityTests
    {
        [Test]
        public void OnlyMatchingKindsFit()
        {
            Assert.AreEqual(ClipFit.Fits, ClipCompatibility.Of(true, true));
            Assert.AreEqual(ClipFit.Fits, ClipCompatibility.Of(false, false));
            Assert.AreEqual(ClipFit.GenericClipOnHumanoid, ClipCompatibility.Of(false, true));
            Assert.AreEqual(ClipFit.HumanoidClipOnGeneric, ClipCompatibility.Of(true, false));
        }

        [Test]
        public void EveryMisfitHasAdviceThatSaysWhatToDo()
        {
            Assert.IsEmpty(ClipCompatibility.Advice(ClipFit.Fits));
            Assert.That(ClipCompatibility.Advice(ClipFit.GenericClipOnHumanoid), Does.Contain("Humanoid").And.Contain("Rig"));
            Assert.That(ClipCompatibility.Advice(ClipFit.HumanoidClipOnGeneric), Does.Contain("Avatar"));
            Assert.That(ClipCompatibility.NoMovement, Does.Contain("frozen pose"));
        }

        [Test]
        public void AFrozenPoseIsNotMovement()
        {
            var rest = new BonePose { Position = new float3(0f, 1f, 0f), Rotation = quaternion.RotateX(0.3f), Scale = 1f };
            Assert.IsFalse(ClipCompatibility.Differs(rest, rest));
            var negated = rest;
            negated.Rotation = new quaternion(-rest.Rotation.value);
            Assert.IsFalse(ClipCompatibility.Differs(rest, negated));
        }

        [Test]
        public void ATurnOrAShiftIsMovement()
        {
            var rest = new BonePose { Position = float3.zero, Rotation = quaternion.identity, Scale = 1f };
            var turned = rest;
            turned.Rotation = quaternion.RotateY(0.01f);
            var moved = rest;
            moved.Position = new float3(0f, 0.001f, 0f);
            Assert.IsTrue(ClipCompatibility.Differs(turned, rest));
            Assert.IsTrue(ClipCompatibility.Differs(moved, rest));
        }
    }
}
