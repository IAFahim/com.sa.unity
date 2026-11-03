using Unity.Entities;
using UnityEngine;

namespace SA.Shape.Unity
{
    [DisallowMultipleComponent]
    public sealed class ShapeMorphRendererAuthoring : MonoBehaviour
    {
        public SkinnedMeshRenderer Body;
    }

    public sealed class ShapeMorphRendererBaker : Baker<ShapeMorphRendererAuthoring>
    {
        public override void Bake(ShapeMorphRendererAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            if (authoring.Body == null)
            {
                Debug.LogWarning("SA: shape '" + authoring.name + "' has a morph renderer authoring without a body. Fix: assign Body, then rebake.", authoring);
                return;
            }
            AddComponent(entity, new ShapeMorphRenderer { Renderer = authoring.Body });
        }
    }
}
