using System;
using Unity.Entities;
using SA.Animation;
using SA.Shape;

namespace SA.Shape.Unity
{
    public struct ShapeDefinitionData
    {
        public AssetId Definition;
        public AssetId Target;
        public ShapeGrounding Grounding;
        public ShapeMarker SupportA;
        public ShapeMarker SupportB;
        public float MinimumLength;
        public float MaximumLength;
        public float MinimumSkinScale;
        public float MaximumSkinScale;
        public BlobArray<ShapeChannel> Channels;
        public BlobArray<OffsetEffect> Offsets;
        public BlobArray<SkinEffect> Skin;
        public BlobArray<SkinFrame> Frames;
        public BlobArray<ShapeMorph> Morphs;
        public BlobArray<MorphEffect> MorphEffects;
    }

    public struct ShapeDefinitionRef : IComponentData
    {
        public BlobAssetReference<ShapeDefinitionData> Value;
    }

    [InternalBufferCapacity(4)]
    public struct ShapeChannelState : IBufferElementData
    {
        public uint Id;
        public float Requested;
        public float Value;
        public float Velocity;
    }

    public struct ShapeState : IComponentData
    {
        public ulong Revision;
    }

    public static class ShapeControl
    {
        public static int FindChannel(DynamicBuffer<ShapeChannelState> channels, uint id)
        {
            for (var i = 0; i < channels.Length; i++)
                if (channels[i].Id == id)
                    return i;
            return -1;
        }

        public static bool SetRequested(DynamicBuffer<ShapeChannelState> channels, uint id, float value)
        {
            var index = FindChannel(channels, id);
            if (index < 0)
                return false;
            var channel = channels[index];
            channel.Requested = value;
            channels[index] = channel;
            return true;
        }

        public static bool TryGetRequested(DynamicBuffer<ShapeChannelState> channels, uint id, out float value)
        {
            value = 0f;
            var index = FindChannel(channels, id);
            if (index < 0)
                return false;
            value = channels[index].Requested;
            return true;
        }
    }

    public static class ShapeRevision
    {
        public const float ChangeTolerance = 1e-6f;

        public static bool HasEffectiveChange(ReadOnlySpan<float> committed, ReadOnlySpan<float> stepped)
        {
            if (committed.Length != stepped.Length)
                return true;
            for (var i = 0; i < stepped.Length; i++)
            {
                var delta = stepped[i] - committed[i];
                if (delta > ChangeTolerance || delta < -ChangeTolerance)
                    return true;
            }
            return false;
        }
    }
}
