using Unity.Entities;

namespace SA
{
    public enum ClipMode : byte { Override, Additive }

    public enum Wrap : byte { Loop, Clamp, PingPong }

    public readonly struct ClipId
    {
        public readonly ushort Index;

        internal ClipId(ushort index) => Index = index;

        public static ClipId None => new ClipId(ushort.MaxValue);
        public bool IsNone => Index == ushort.MaxValue;
        public bool Is(ClipId other) => Index == other.Index;
    }

    public readonly struct MaskId
    {
        public readonly ushort Index;

        internal MaskId(ushort index) => Index = index;

        public static MaskId Full => new MaskId(0);
        public bool Is(MaskId other) => Index == other.Index;
    }

    [InternalBufferCapacity(4)]
    public struct ClipState : IBufferElementData
    {
        public ClipId Clip;
        public byte Layer;
        public ClipMode Mode;
        public MaskId Mask;
        public Wrap Wrap;
        public bool Synced;
        public float Phase;
        public float Speed;
        public float Weight;
        public float Fade;
        public float FadeRate;
    }

    public readonly struct PlayOptions
    {
        public readonly byte Layer;
        public readonly ClipMode Mode;
        public readonly MaskId Mask;
        public readonly Wrap Wrap;
        public readonly float Speed;
        public readonly bool Restart;

        public PlayOptions(byte layer, ClipMode mode, MaskId mask, Wrap wrap, float speed, bool restart)
        {
            Layer = layer;
            Mode = mode;
            Mask = mask;
            Wrap = wrap;
            Speed = speed;
            Restart = restart;
        }

        public static PlayOptions Default => new PlayOptions(0, ClipMode.Override, MaskId.Full, Wrap.Loop, 1f, false);

        public PlayOptions WithLayer(byte layer) => new PlayOptions(layer, Mode, Mask, Wrap, Speed, Restart);
        public PlayOptions WithMode(ClipMode mode) => new PlayOptions(Layer, mode, Mask, Wrap, Speed, Restart);
        public PlayOptions WithMask(MaskId mask) => new PlayOptions(Layer, Mode, mask, Wrap, Speed, Restart);
        public PlayOptions WithWrap(Wrap wrap) => new PlayOptions(Layer, Mode, Mask, wrap, Speed, Restart);
        public PlayOptions WithSpeed(float speed) => new PlayOptions(Layer, Mode, Mask, Wrap, speed, Restart);
        public PlayOptions WithRestart(bool restart) => new PlayOptions(Layer, Mode, Mask, Wrap, Speed, restart);
    }
}
