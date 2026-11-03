using System;
using UnityEngine;
using SA.Animation;
using SA.Animation.Content;
using SA.Motion.Numerics;

namespace SA.Shape.Unity.Editor
{
    [Serializable]
    public struct ShapeInspectorRow
    {
        public uint Id;
        public string Name;
        public float Minimum;
        public float Maximum;
        public float Default;
        public float Requested;
        public bool HasRequest;
    }

    [Serializable]
    sealed class ShapeChannelLabel
    {
        public uint id;
        public string name;
    }

    [Serializable]
    sealed class ShapeChannelLabelList
    {
        public ShapeChannelLabel[] channels;
    }

    public static class ShapeInspectorModel
    {
        public static bool Describe(TextAsset source, string[] boneNames, string[] meshMorphs, AssetId expectedTarget, Float3[] baseOffsets, ShapeRequest[] requests, out ShapeAsset asset, out ShapeInspectorRow[] rows, out string warning, out string error)
        {
            asset = null;
            rows = Array.Empty<ShapeInspectorRow>();
            warning = string.Empty;
            error = string.Empty;
            if (source == null)
            {
                error = "no shape definition assigned. Assign a .sashape TextAsset, then rebake.";
                return false;
            }
            if (boneNames == null)
                boneNames = Array.Empty<string>();
            if (meshMorphs == null)
                meshMorphs = Array.Empty<string>();
            if (baseOffsets == null)
                baseOffsets = Array.Empty<Float3>();
            if (requests == null)
                requests = Array.Empty<ShapeRequest>();
            var parse = ShapeFile.Parse(source.bytes, boneNames, meshMorphs, expectedTarget, baseOffsets, out var parsed);
            if (parse != AnimationError.None || parsed == null)
            {
                if (HasAuthoredMorphs(source.text))
                    error = "shape definition is not supported (" + parse + "). Its corrective morphs need matching mesh blend shapes: add a SkinAuthoring mesh with those names, then rebake.";
                else
                    error = "shape definition is not supported (" + parse + "). Fix the .sashape source or pick the matching rig, then rebake.";
                return false;
            }
            asset = parsed;
            if (asset.Frames.Length != 0 && asset.Frames.Length != boneNames.Length)
            {
                error = "shape definition has " + asset.Frames.Length + " skin frames for " + boneNames.Length + " rig bones. Provide one frame per bone or none, then rebake.";
                return false;
            }
            var labels = ShapeChannelLabelListFor(source.text);
            rows = new ShapeInspectorRow[asset.Channels.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var channel = asset.Channels[i];
                var requested = channel.Default;
                var hasRequest = false;
                for (var r = 0; r < requests.Length; r++)
                    if (requests[r].Id == channel.Id)
                    {
                        requested = requests[r].Value;
                        hasRequest = true;
                        break;
                    }
                rows[i] = new ShapeInspectorRow
                {
                    Id = channel.Id,
                    Name = DisplayName(labels, requests, channel.Id),
                    Minimum = channel.Minimum,
                    Maximum = channel.Maximum,
                    Default = channel.Default,
                    Requested = requested,
                    HasRequest = hasRequest
                };
            }
            var unknown = 0;
            for (var r = 0; r < requests.Length; r++)
            {
                var known = false;
                for (var i = 0; i < asset.Channels.Length; i++)
                    if (asset.Channels[i].Id == requests[r].Id)
                    {
                        known = true;
                        break;
                    }
                if (!known)
                    unknown++;
            }
            if (unknown > 0)
                warning = unknown + " saved requests match no definition channel and stay unused. Drop them or fix their ids, then rebake.";
            return true;
        }

        static bool HasAuthoredMorphs(string json)
        {
            if (string.IsNullOrEmpty(json))
                return false;
            var key = json.IndexOf("\"morphs\"", StringComparison.Ordinal);
            if (key < 0)
                return false;
            var open = json.IndexOf('[', key);
            if (open < 0)
                return false;
            for (var i = open + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (c == ']')
                    return false;
                if (!char.IsWhiteSpace(c))
                    return true;
            }
            return false;
        }

        static ShapeChannelLabel[] ShapeChannelLabelListFor(string json)
        {
            try
            {
                var list = JsonUtility.FromJson<ShapeChannelLabelList>(json);
                if (list == null || list.channels == null)
                    return Array.Empty<ShapeChannelLabel>();
                return list.channels;
            }
            catch (Exception)
            {
                return Array.Empty<ShapeChannelLabel>();
            }
        }

        static string DisplayName(ShapeChannelLabel[] labels, ShapeRequest[] requests, uint id)
        {
            for (var i = 0; i < labels.Length; i++)
                if (labels[i] != null && labels[i].id == id && !string.IsNullOrEmpty(labels[i].name))
                    return labels[i].name;
            for (var r = 0; r < requests.Length; r++)
                if (requests[r].Id == id && !string.IsNullOrEmpty(requests[r].Name))
                    return requests[r].Name;
            return "channel " + id;
        }
    }
}
