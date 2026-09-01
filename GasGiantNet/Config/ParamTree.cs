using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GasGiantNet.Config
{
    internal sealed class ParamTree
    {
        private readonly JsonObject _root;
        private readonly ConcurrentDictionary<string, JsonNode> _nodeCache = new ConcurrentDictionary<string, JsonNode>();

        private ParamTree(JsonObject root)
        {
            _root = root;
        }

        public static ParamTree LoadResolvedPreset(string presetName, string baseDirectory)
        {
            string path = Path.Combine(baseDirectory, "PresetsResolved", presetName + ".json");
            if (!File.Exists(path))
                throw new ArgumentException("unknown preset '" + presetName + "'");
            return FromJson(File.ReadAllText(path));
        }

        public static ParamTree LoadPresetFile(string path, string defaultsPath)
        {
            JsonObject defaults = ParseObject(File.ReadAllText(defaultsPath));
            JsonObject doc = ParseObject(File.ReadAllText(path));
            JsonNode paramsNode;
            if (doc.TryGetPropertyValue("params", out paramsNode) && paramsNode is JsonObject)
                doc = (JsonObject)paramsNode;
            DeepMerge(defaults, doc);
            ParamTree result = new ParamTree(defaults);
            if (result.Has("mask.file"))
            {
                string mask = result.NullableString("mask.file");
                if (!string.IsNullOrEmpty(mask) && !Path.IsPathRooted(mask))
                {
                    string parent = Path.GetDirectoryName(Path.GetFullPath(path));
                    result.SetString("mask.file", Path.GetFullPath(Path.Combine(parent, mask)));
                }
            }
            return result;
        }

        public static ParamTree FromJson(string json)
        {
            return new ParamTree(ParseObject(json));
        }

        private static JsonObject ParseObject(string json)
        {
            JsonNode node = JsonNode.Parse(json);
            JsonObject obj = node as JsonObject;
            if (obj == null) throw new ArgumentException("JSON root must be an object");
            return obj;
        }

        private static void DeepMerge(JsonObject target, JsonObject source)
        {
            foreach (KeyValuePair<string, JsonNode> pair in source)
            {
                JsonNode existing;
                if (pair.Value is JsonObject && target.TryGetPropertyValue(pair.Key, out existing) && existing is JsonObject)
                {
                    DeepMerge((JsonObject)existing, (JsonObject)pair.Value);
                }
                else
                {
                    target[pair.Key] = pair.Value == null ? null : pair.Value.DeepClone();
                }
            }
        }

        private JsonNode Node(string path)
        {
            JsonNode cached;
            if (_nodeCache.TryGetValue(path, out cached))
                return cached;

            string[] parts = path.Split('.');
            JsonNode cur = _root;
            for (int i = 0; i < parts.Length; i++)
            {
                JsonObject obj = cur as JsonObject;
                if (obj == null) throw new KeyNotFoundException(path);
                JsonNode next;
                if (!obj.TryGetPropertyValue(parts[i], out next)) throw new KeyNotFoundException(path);
                cur = next;
            }

            // ConcurrentDictionary does not accept null values.
            if (cur != null) _nodeCache.TryAdd(path, cur);
            return cur;
        }

        public bool Has(string path)
        {
            try { return Node(path) != null; }
            catch { return false; }
        }

        public int Int(string path) { return Node(path).GetValue<int>(); }
        public long Long(string path) { return Node(path).GetValue<long>(); }
        public float Float(string path) { return (float)Node(path).GetValue<double>(); }
        public double Double(string path) { return Node(path).GetValue<double>(); }
        public bool Bool(string path) { return Node(path).GetValue<bool>(); }
        public string String(string path) { return Node(path).GetValue<string>(); }

        public float? NullableFloat(string path)
        {
            JsonNode n = Node(path);
            return n == null ? (float?)null : (float)n.GetValue<double>();
        }

        public int? NullableInt(string path)
        {
            JsonNode n = Node(path);
            return n == null ? (int?)null : n.GetValue<int>();
        }

        public string NullableString(string path)
        {
            JsonNode n = Node(path);
            return n == null ? null : n.GetValue<string>();
        }

        public float[] FloatArray(string path)
        {
            JsonArray a = Node(path) as JsonArray;
            if (a == null) throw new InvalidOperationException(path + " is not an array");
            float[] result = new float[a.Count];
            for (int i = 0; i < result.Length; i++) result[i] = (float)a[i].GetValue<double>();
            return result;
        }

        public double[] DoubleArray(string path)
        {
            JsonArray a = Node(path) as JsonArray;
            if (a == null) throw new InvalidOperationException(path + " is not an array");
            double[] result = new double[a.Count];
            for (int i = 0; i < result.Length; i++) result[i] = a[i].GetValue<double>();
            return result;
        }

        public JsonArray Array(string path)
        {
            JsonArray a = Node(path) as JsonArray;
            if (a == null) throw new InvalidOperationException(path + " is not an array");
            return a;
        }

        public JsonObject Object(string path)
        {
            JsonObject a = Node(path) as JsonObject;
            if (a == null) throw new InvalidOperationException(path + " is not an object");
            return a;
        }

        public void SetInt(string path, int value) { Set(path, JsonValue.Create(value)); }
        public void SetFloat(string path, float value) { Set(path, JsonValue.Create(value)); }
        public void SetBool(string path, bool value) { Set(path, JsonValue.Create(value)); }
        public void SetString(string path, string value) { Set(path, JsonValue.Create(value)); }

        private void Set(string path, JsonNode value)
        {
            string[] parts = path.Split('.');
            JsonObject obj = _root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                JsonNode n;
                if (!obj.TryGetPropertyValue(parts[i], out n) || !(n is JsonObject))
                {
                    JsonObject child = new JsonObject();
                    obj[parts[i]] = child;
                    obj = child;
                }
                else obj = (JsonObject)n;
            }
            obj[parts[parts.Length - 1]] = value;
            _nodeCache.Clear();
        }

        public string ToJson()
        {
            return _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
