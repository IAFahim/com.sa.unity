using Unity.Entities;
using UnityEngine;

public struct Crowd : IComponentData
{
    public Entity Prefab;
    public int Count;
    public float Spacing;
}

public struct CrowdMember : IComponentData { }

public class CrowdAuthoring : MonoBehaviour
{
    public GameObject Prefab;
    public int Count;
    public float Spacing = 1.5f;
}

public class CrowdBaker : Baker<CrowdAuthoring>
{
    public override void Bake(CrowdAuthoring authoring) =>
        AddComponent(GetEntity(TransformUsageFlags.None), new Crowd
        {
            Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
            Count = authoring.Count,
            Spacing = authoring.Spacing
        });
}
