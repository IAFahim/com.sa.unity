using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using SA.Animation;
using SA.Animation.Content;
using SA.Editor;
using UnityEngine;
using Float3 = SA.Motion.Numerics.Float3;
using MQuaternion = SA.Motion.Numerics.Quaternion;
using Pose = SA.Motion.Numerics.Pose;

namespace SA.Tests
{
    public class PilotBakeTests
    {
        const float QuatTol = 1e-3f;
        const float RootTol = 1e-6f;

        [Test]
        public void PilotClipsDecodeFiniteAndRecordFamilies()
        {
            var manifest = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/sa/c08/_manifest.json"));
            if (!File.Exists(manifest))
                Assert.Ignore("Pilot bake awaiting extractor: " + manifest + " not present.");
            var directory = Path.GetDirectoryName(manifest);
            var errors = new List<string>();
            var report = new StringBuilder(4096);
            report.Append("{\"clips\":[");
            var first = true;
            Travels.Clear();
            var document = Json.Read(File.ReadAllText(manifest));
            foreach (var entry in document.GetList("clips"))
            {
                var entryMap = (Dictionary<string, object>)entry;
                var rig = (string)entryMap["rig"];
                var clip = (string)entryMap["clip"];
                var file = (string)entryMap["file"];
                if (!first)
                    report.Append(',');
                first = false;
                try
                {
                    report.Append(CheckClip(directory, rig, clip, file));
                }
                catch (Exception exception)
                {
                    errors.Add(rig + "/" + clip + ": " + exception.Message);
                    report.Append("{\"rig\":" + Quote(rig) + ",\"clip\":" + Quote(clip) + ",\"error\":" + Quote(exception.Message) + "}");
                }
            }
            report.Append("]}");
            File.WriteAllText(Path.Combine(directory, "../pilot-families.json"), report.ToString());
            Debug.Log("SA: pilot bake: " + report);
            AssertTravelPair("Dual_Blade@Walk_Fwd/Walk_Fwd", "Dual_Blade@Walk_Fwd_Root/Walk_Fwd_Root");
            AssertTravelPair("Dual_Blade@Run/Run", "Dual_Blade@Run_Root/Run_Root");
            Assert.IsEmpty(errors, "pilot clips failed:\n" + string.Join("\n", errors));
        }

        static readonly Dictionary<string, double> Travels = new Dictionary<string, double>();

        static void AssertTravelPair(string inplaceKey, string rootKey)
        {
            if (!Travels.TryGetValue(inplaceKey, out var inplace) || !Travels.TryGetValue(rootKey, out var root))
                throw new Exception("travel pair missing: " + inplaceKey + " / " + rootKey);
            if (inplace > 1e-6)
                throw new Exception(inplaceKey + " should be inplace but travels " + inplace.ToString("G9") + "m");
            if (root < 1.0)
                throw new Exception(rootKey + " should travel but only moves " + root.ToString("G9") + "m");
            if (root - inplace < 1.0)
                throw new Exception("inplace/root twins did not separate: " + inplaceKey + "=" + inplace.ToString("G9") + "m " + rootKey + "=" + root.ToString("G9") + "m");
        }

        static string CheckClip(string directory, string rig, string clip, string file)
        {
            var path = Path.Combine(directory, file);
            var root = Json.Read(File.ReadAllText(path));
            var bones = root.GetList("bones");
            var frames = root.GetList("frames");
            var bonesCount = bones.Count;
            if (bonesCount < 1 || bonesCount > 1024)
                throw new Exception("bones out of range: " + bonesCount);
            var sampleCount = root.GetInt("sampleCount");
            if (sampleCount != frames.Count)
                throw new Exception("frames has " + frames.Count + " poses, need sampleCount=" + sampleCount);
            var duration = root.GetDouble("duration");
            if (duration <= 0 || sampleCount < 2)
                throw new Exception("bad duration/sampleCount: " + duration + "/" + sampleCount);
            var loop = root.GetBool("loop");
            var loopTime = root.GetBool("loopTime");
            var maskRoot = root.GetInt("maskRoot");
            var syntheticRoot = root.GetBool("syntheticRoot");
            var scaleNormalized = root.GetBool("scaleNormalized");
            var rawGap = root.GetDouble("worstScaleGap");
            var rawBone = (string)root["worstScaleBone"];
            if (!loop || !loopTime)
                throw new Exception("import fidelity: loop/loopTime must be true (reapply take settings and rebake)");
            var names = new string[bonesCount];
            var parents = new int[bonesCount];
            for (var i = 0; i < bonesCount; i++)
            {
                var bone = (Dictionary<string, object>)bones[i];
                names[i] = (string)bone["path"];
                var parent = Convert.ToInt32(bone["parent"]);
                parents[i] = i == 0 && parent == 0 ? -1 : parent;
                if (parents[i] >= i)
                    throw new Exception("bone " + i + " breaks parent-before-child order");
            }
            if (new HashSet<string>(names).Count != bonesCount)
                throw new Exception("bone paths are not unique");
            if (!syntheticRoot)
                throw new Exception("bone-zero: extractor identity root missing");
            if (names[0] != SA.Editor.RigBuilder.SyntheticRootName || parents[0] != -1)
                throw new Exception("bone-zero: bones[0] must be the synthetic identity root");
            var samples = new Pose[bonesCount * sampleCount];
            var rootMotion = new RigidPose[sampleCount];
            for (var f = 0; f < sampleCount; f++)
            {
                var frame = (Dictionary<string, object>)frames[f];
                var p = (List<object>)frame["p"];
                var q = (List<object>)frame["q"];
                var s = (List<object>)frame["s"];
                if (p.Count != bonesCount || q.Count != bonesCount || s.Count != bonesCount)
                    throw new Exception("frame " + f + " has wrong track count");
                for (var b = 0; b < bonesCount; b++)
                {
                    var position = Float3Of((List<object>)p[b]);
                    var rotation = QuatOf((List<object>)q[b]);
                    var scale = Float3Of((List<object>)s[b]);
                    if (!Finite(position) || !Finite(rotation) || !Finite(scale))
                        throw new Exception("frame " + f + " bone " + b + " is not finite");
                    if (Math.Abs(RotationLength(rotation) - 1f) > QuatTol)
                        throw new Exception("frame " + f + " bone " + b + " rotation is not unit");
                    if (Math.Max(Math.Abs(scale.X - 1f), Math.Max(Math.Abs(scale.Y - 1f), Math.Abs(scale.Z - 1f))) > 1e-5f)
                        throw new Exception("frame " + f + " bone " + b + " scale is not unit (extractor must normalize)");
                    samples[f * bonesCount + b] = new Pose { Position = position, Rotation = rotation, Scale = Float3.One };
                }
                var rp = (List<object>)frame["rootP"];
                var rq = (List<object>)frame["rootQ"];
                var rootPosition = new Float3(ToFloat(rp[0]), ToFloat(rp[1]), ToFloat(rp[2]));
                var rootRotation = new MQuaternion(ToFloat(rq[0]), ToFloat(rq[1]), ToFloat(rq[2]), ToFloat(rq[3]));
                if (!Finite(rootPosition) || !Finite(rootRotation))
                    throw new Exception("frame " + f + " root is not finite");
                rootMotion[f] = new RigidPose(rootPosition, rootRotation);
            }
            if (rootMotion[0].Position.LengthSquared > RootTol * RootTol || Math.Abs(rootMotion[0].Rotation.W) < 1f - RootTol)
                throw new Exception("root[0] is not identity");
            for (var f = 0; f < sampleCount; f++)
                if (!IsIdentityPose(samples[f * bonesCount]))
                    throw new Exception("bone-zero: synthetic root is not identity at frame " + f);
            var policy = RootPolicy.None;
            foreach (var pose in rootMotion)
            {
                if (RootIsIdentity(pose))
                    continue;
                policy = RootIsPlanar(pose) && policy != RootPolicy.FullRigid ? RootPolicy.Planar : RootPolicy.FullRigid;
            }
            var reference = new Pose[bonesCount];
            Array.Copy(samples, reference, bonesCount);
            if (ContentIdentity.Skeleton(rig, 1, names, parents, reference, out var family) != AnimationError.None)
                throw new Exception("rig-bound family identity failed");
            if (ContentIdentity.Skeleton("Pilot", 1, names, parents, reference, out var skeleton) != AnimationError.None)
                throw new Exception("skeleton identity failed");
            var encode = SharedClipEncoder.Encode(family, parents, reference, samples, sampleCount, duration, loop, policy, rootMotion,
                Array.Empty<Pose>(), Array.Empty<float>(), Array.Empty<PoseMarker>(), true, CompressionLimits.Default, out var bytes, out var encodeReport);
            if (encode != AnimationError.None)
                throw new Exception("raw encode failed (" + encode + ")");
            if (SharedClipReader.Load(bytes, family, reference, out var decoded) != AnimationError.None || decoded == null)
                throw new Exception("encoded clip failed to reload");
            var actual = new Pose[bonesCount];
            for (var f = 0; f < sampleCount; f++)
            {
                if (decoded.Sample(duration * f / (sampleCount - 1), reference, actual) != AnimationError.None)
                    throw new Exception("sample " + f + " failed");
                for (var b = 0; b < bonesCount; b++)
                    if (!Finite(actual[b].Position) || !Finite(actual[b].Rotation))
                        throw new Exception("sample " + f + " bone " + b + " decoded non-finite");
            }
            for (var k = 1; k < 4; k++)
            {
                if (decoded.Sample(duration * k / 4, reference, actual) != AnimationError.None)
                    throw new Exception("quarter sample " + k + " failed");
            }
            var scorer = ScoreRequired(names);
            var travel = (rootMotion[sampleCount - 1].Position - rootMotion[0].Position).Length;
            var turn = 2f * (float)Math.Acos(Math.Min(1.0, Math.Abs(MQuaternion.Dot(rootMotion[0].Rotation, rootMotion[sampleCount - 1].Rotation))));
            Travels[rig + "/" + clip] = travel;
            int wantMask;
            if (clip == "Scene")
                wantMask = -1;
            else if (clip == "Idle" || clip == "Walk_Fwd")
                wantMask = 0;
            else
                wantMask = 1;
            if (maskRoot != wantMask)
                throw new Exception("maskRoot=" + maskRoot + ", want " + wantMask + " (per-take mask lost at import)");
            if (scaleNormalized != rawGap > 1e-5)
                throw new Exception("scaleNormalized=" + scaleNormalized + " disagrees with worstScaleGap=" + rawGap.ToString("G9"));
            if (scaleNormalized && string.IsNullOrEmpty(rawBone))
                throw new Exception("scaleNormalized without a reported bone");
            Debug.Log("SA: pilot " + rig + "/" + clip + ": bones=" + bonesCount + " syntheticRoot=" + syntheticRoot
                + " samples=" + sampleCount + " policy=" + policy + " bytes=" + encodeReport.EncodedBytes + " travel=" + travel.ToString("G6") + "m turn=" + turn.ToString("G6") + "rad worstScale=" + rawGap.ToString("G6") + "@" + rawBone
                + " loopTime=" + loopTime + " maskRoot=" + maskRoot
                + " family=" + SharedFamilyBake.ToHex(family) + " skeleton=" + SharedFamilyBake.ToHex(skeleton) + " scorer=" + scorer);
            return "{\"rig\":" + Quote(rig) + ",\"clip\":" + Quote(clip) + ",\"file\":" + Quote(file)
                + ",\"bones\":" + bonesCount + ",\"syntheticRoot\":" + (syntheticRoot ? "true" : "false")
                + ",\"samples\":" + sampleCount + ",\"duration\":" + duration.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"loop\":" + (loop ? "true" : "false") + ",\"loopTime\":" + (loopTime ? "true" : "false") + ",\"maskRoot\":" + maskRoot + ",\"policy\":\"" + policy + "\""
                + ",\"bytes\":" + encodeReport.EncodedBytes
                + ",\"travelM\":" + travel.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"turnRad\":" + turn.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"scaleNormalized\":" + (scaleNormalized ? "true" : "false")
                + ",\"worstScaleGap\":" + rawGap.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) + ",\"worstScaleBone\":" + Quote(rawBone)
                + ",\"family\":" + Quote(SharedFamilyBake.ToHex(family)) + ",\"skeleton\":" + Quote(SharedFamilyBake.ToHex(skeleton))
                + ",\"scorer\":" + Quote(scorer) + "}";
        }

        static string ScoreRequired(string[] names)
        {
            var canonical = SharedFamilyBake.CanonicalNames;
            var required = SharedFamilyBake.RequiredRoles;
            var leaves = new string[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var slash = names[i].LastIndexOf('/');
                leaves[i] = slash >= 0 ? names[i].Substring(slash + 1) : names[i];
            }
            var parts = new List<string>();
            foreach (var role in required)
            {
                var best = -1;
                var bestBone = string.Empty;
                var second = -1;
                for (var b = 0; b < leaves.Length; b++)
                {
                    var score = SharedFamilyBake.ScoreBoneName(canonical[role], leaves[b]);
                    if (score > best)
                    {
                        second = best;
                        best = score;
                        bestBone = leaves[b];
                    }
                    else if (score > second)
                    {
                        second = score;
                    }
                }
                if (best <= 0)
                    throw new Exception("required role " + canonical[role] + " has no bone match");
                parts.Add(canonical[role] + "=" + bestBone + ":" + best + "(2nd:" + second + ")");
            }
            return string.Join(";", parts);
        }

        static bool RootIsIdentity(in RigidPose pose) =>
            pose.Position.LengthSquared <= RootTol * RootTol && Math.Abs(pose.Rotation.W) >= 1f - RootTol;
        static bool IsIdentityPose(in Pose pose) =>
            pose.Position.LengthSquared <= RootTol * RootTol && Math.Abs(pose.Rotation.W) >= 1f - RootTol;
        static Float3 Float3Of(List<object> values) => new Float3(ToFloat(values[0]), ToFloat(values[1]), ToFloat(values[2]));
        static bool RootIsPlanar(in RigidPose pose) =>
            Math.Abs(pose.Position.Y) <= 1e-6f && Math.Abs(pose.Rotation.X) <= 1e-6f && Math.Abs(pose.Rotation.Z) <= 1e-6f;

        static MQuaternion QuatOf(List<object> values) => new MQuaternion(ToFloat(values[0]), ToFloat(values[1]), ToFloat(values[2]), ToFloat(values[3]));

        static float RotationLength(MQuaternion rotation) => (float)Math.Sqrt(rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z + rotation.W * rotation.W);

        static bool Finite(Float3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        static bool Finite(MQuaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

        static float ToFloat(object value) => Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);

        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static class Json
        {
            public static Dictionary<string, object> Read(string text)
            {
                var parser = new Parser(text);
                var value = parser.ReadValue();
                if (value is Dictionary<string, object> map)
                    return map;
                throw new Exception("manifest root is not an object");
            }

            sealed class Parser
            {
                readonly string text;
                int at;

                public Parser(string text)
                {
                    this.text = text;
                }

                public object ReadValue()
                {
                    Skip();
                    if (at >= text.Length)
                        throw new Exception("unexpected end of JSON");
                    var glyph = text[at];
                    if (glyph == '{')
                        return ReadObject();
                    if (glyph == '[')
                        return ReadArray();
                    if (glyph == '"')
                        return ReadString();
                    if (glyph == 't' || glyph == 'f')
                        return ReadBool();
                    return ReadNumber();
                }

                Dictionary<string, object> ReadObject()
                {
                    var map = new Dictionary<string, object>();
                    at++;
                    Skip();
                    if (Peek('}'))
                    {
                        at++;
                        return map;
                    }
                    while (true)
                    {
                        Skip();
                        var key = ReadString();
                        Skip();
                        Expect(':');
                        map[key] = ReadValue();
                        Skip();
                        if (Peek('}'))
                        {
                            at++;
                            return map;
                        }
                        Expect(',');
                    }
                }

                List<object> ReadArray()
                {
                    var items = new List<object>();
                    at++;
                    Skip();
                    if (Peek(']'))
                    {
                        at++;
                        return items;
                    }
                    while (true)
                    {
                        items.Add(ReadValue());
                        Skip();
                        if (Peek(']'))
                        {
                            at++;
                            return items;
                        }
                        Expect(',');
                    }
                }

                string ReadString()
                {
                    var output = new StringBuilder();
                    at++;
                    while (true)
                    {
                        if (at >= text.Length)
                            throw new Exception("unterminated string");
                        var glyph = text[at++];
                        if (glyph == '"')
                            return output.ToString();
                        if (glyph == '\\')
                        {
                            if (at >= text.Length)
                                throw new Exception("unterminated escape");
                            var escape = text[at++];
                            if (escape == 'n')
                                output.Append('\n');
                            else if (escape == 't')
                                output.Append('\t');
                            else if (escape == 'u')
                            {
                                output.Append((char)Convert.ToInt32(text.Substring(at, 4), 16));
                                at += 4;
                            }
                            else
                                output.Append(escape);
                        }
                        else
                        {
                            output.Append(glyph);
                        }
                    }
                }

                bool ReadBool()
                {
                    if (text.Substring(at, 4) == "true")
                    {
                        at += 4;
                        return true;
                    }
                    if (text.Substring(at, 5) == "false")
                    {
                        at += 5;
                        return false;
                    }
                    throw new Exception("bad literal at " + at);
                }

                double ReadNumber()
                {
                    var start = at;
                    while (at < text.Length && "-+0123456789.eE".IndexOf(text[at]) >= 0)
                        at++;
                    return double.Parse(text.Substring(start, at - start), System.Globalization.CultureInfo.InvariantCulture);
                }

                void Skip()
                {
                    while (at < text.Length && char.IsWhiteSpace(text[at]))
                        at++;
                }

                bool Peek(char glyph) => at < text.Length && text[at] == glyph;

                void Expect(char glyph)
                {
                    if (!Peek(glyph))
                        throw new Exception("expected '" + glyph + "' at " + at);
                    at++;
                }
            }
        }
    }

    static class PilotBakeDocument
    {
        public static List<object> GetList(this Dictionary<string, object> map, string key)
        {
            if (map.TryGetValue(key, out var value) && value is List<object> items)
                return items;
            throw new Exception("missing list \"" + key + "\"");
        }

        public static int GetInt(this Dictionary<string, object> map, string key) => Convert.ToInt32(map[key], System.Globalization.CultureInfo.InvariantCulture);

        public static double GetDouble(this Dictionary<string, object> map, string key) => Convert.ToDouble(map[key], System.Globalization.CultureInfo.InvariantCulture);

        public static bool GetBool(this Dictionary<string, object> map, string key) => (bool)map[key];
    }
}
