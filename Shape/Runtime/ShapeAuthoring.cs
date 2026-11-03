using System;
using System.Collections.Generic;
using UnityEngine;
using SA;
using SA.Shape;
namespace SA.Shape.Unity
{
    [Serializable]
    public struct ShapeRequest
    {
        public uint Id;
        public string Name;
        public float Value;
    }

    [DisallowMultipleComponent]
    public class ShapeAuthoring : MonoBehaviour
    {
        public SharedFamilyAuthoring Family;
        public ShapeDefinitionAsset Definition;
        public bool LivePhysical;
        public ShapeMassPolicy MassPolicy;
        public List<string> Wardrobe = new List<string>();
        public List<ShapeRequest> Requests = new List<ShapeRequest>();
    }
}
