using Unity.Mathematics;

namespace SA.Editor
{
    public enum ClipFit : byte
    {
        Fits,
        GenericClipOnHumanoid,
        HumanoidClipOnGeneric
    }

    public static class ClipCompatibility
    {
        const float RotationDot = 0.999999f;
        const float PositionSquared = 1e-8f;

        public const string NoMovement = "does not move any bone of this character, so it plays as a frozen pose. Its bones are probably named differently. Set the animation's Rig to Humanoid, or use a clip made for this skeleton.";

        public static ClipFit Of(bool clipIsHumanoid, bool avatarIsHumanoid) =>
            avatarIsHumanoid && !clipIsHumanoid ? ClipFit.GenericClipOnHumanoid
            : !avatarIsHumanoid && clipIsHumanoid ? ClipFit.HumanoidClipOnGeneric
            : ClipFit.Fits;

        public static string Advice(ClipFit fit) => fit switch
        {
            ClipFit.GenericClipOnHumanoid => "is a Generic clip but this character is Humanoid, so it cannot move it and plays as a frozen pose. Set the animation's Rig to Humanoid (select its model, Rig tab, Animation Type Humanoid), or use a clip made for this skeleton.",
            ClipFit.HumanoidClipOnGeneric => "is a Humanoid clip but this character's Avatar is not Humanoid, so it cannot move it and plays as a frozen pose. Give the character a Humanoid Avatar, or use a Generic clip made for this skeleton.",
            _ => string.Empty
        };

        public static bool Differs(in BonePose a, in BonePose b) =>
            math.abs(math.dot(a.Rotation.value, b.Rotation.value)) < RotationDot || math.lengthsq(a.Position - b.Position) > PositionSquared;
    }
}
