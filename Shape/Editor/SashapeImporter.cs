using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace SA.Shape.Unity.Editor
{
    [ScriptedImporter(2, "sashape")]
    public class SashapeImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var asset = new TextAsset(File.ReadAllText(context.assetPath));
            context.AddObjectToAsset("text", asset);
            context.SetMainObject(asset);
        }
    }
}
