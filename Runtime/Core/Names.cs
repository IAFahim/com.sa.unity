using System;
using Unity.Collections;
using Unity.Entities;

namespace SA
{
    public static class Names
    {
        public static unsafe uint Hash(in FixedString64Bytes name)
        {
            var copy = name;
            return xxHash3.Hash64(copy.GetUnsafePtr(), copy.Length).x;
        }

        public static ClipId Clip(ReadOnlySpan<ClipAsset> clips, uint nameHash)
        {
            for (var i = 0; i < clips.Length; i++)
                if (clips[i].Value.IsCreated && clips[i].Value.Value.NameHash == nameHash)
                    return new ClipId((ushort)i);
            return ClipId.None;
        }

        public static ClipId Clip(ReadOnlySpan<ClipAsset> clips, in FixedString64Bytes name) => Clip(clips, Hash(name));

        public static ClipId Clip(in DynamicBuffer<ClipAsset> clips, in FixedString64Bytes name) => Clip(clips.AsNativeArray().AsReadOnlySpan(), name);

        public static MaskId Mask(ReadOnlySpan<MaskAsset> masks, uint nameHash)
        {
            for (var i = 0; i < masks.Length; i++)
                if (masks[i].Value.IsCreated && masks[i].Value.Value.NameHash == nameHash)
                    return new MaskId((ushort)i);
            return MaskId.Full;
        }

        public static MaskId Mask(ReadOnlySpan<MaskAsset> masks, in FixedString64Bytes name) => Mask(masks, Hash(name));

        public static MaskId Mask(in DynamicBuffer<MaskAsset> masks, in FixedString64Bytes name) => Mask(masks.AsNativeArray().AsReadOnlySpan(), name);

        public static int Bone(ref RigBlob rig, in FixedString64Bytes name)
        {
            var hash = Hash(name);
            for (var i = 0; i < rig.Bones.Length; i++)
                if (rig.Bones[i].NameHash == hash)
                    return i;
            return -1;
        }

        public static ClipId ClipAt(ReadOnlySpan<ClipAsset> clips, int index) =>
            index >= 0 && index < clips.Length && index < ushort.MaxValue ? new ClipId((ushort)index) : ClipId.None;

        public static MaskId MaskAt(ReadOnlySpan<MaskAsset> masks, int index) =>
            index >= 0 && index < masks.Length && index <= ushort.MaxValue ? new MaskId((ushort)index) : MaskId.Full;
    }
}
