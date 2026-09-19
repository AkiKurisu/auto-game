#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace AutoGame
{
    public static class UnityValueNormalizer
    {
        const int MaxNormalizedDepth = 4;
        const int MaxNormalizedItems = 32;

        public static AutoGameValue Normalize(object value) { return NormalizeValue(value, 0); }

        static AutoGameValue NormalizeValue(object value, int depth)
        {
            if (value == null) return AutoGameValue.Null;
            if (value is AutoGameValue) return (AutoGameValue)value;
            if (depth >= MaxNormalizedDepth) return AutoGameValue.FromString(value.ToString());
            if (value is string || value is char) return AutoGameValue.FromString(value.ToString());
            if (value is bool) return AutoGameValue.FromBoolean((bool)value);
            if (value is sbyte || value is short || value is int || value is long)
                return AutoGameValue.FromInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (value is byte || value is ushort || value is uint || value is ulong)
                return AutoGameValue.FromUInt64(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
            if (value is float || value is double || value is decimal)
                return AutoGameValue.FromDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            if (value is DateTime) return AutoGameValue.FromString(((DateTime)value).ToString("O", CultureInfo.InvariantCulture));
            if (value is DateTimeOffset) return AutoGameValue.FromString(((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture));
            if (value is Guid) return AutoGameValue.FromString(((Guid)value).ToString("D"));

            var type = value.GetType();
            if (type.IsEnum) return AutoGameValue.FromString(value.ToString());
            var unity = NormalizeUnityValue(value, type, depth);
            if (unity != null) return unity;

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var map = NewObject();
                var count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= MaxNormalizedItems) break;
                    map[entry.Key == null ? string.Empty : entry.Key.ToString()] = NormalizeValue(entry.Value, depth + 1);
                }
                return AutoGameValue.FromObject(map);
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                var array = new List<AutoGameValue>();
                foreach (var item in enumerable)
                {
                    if (array.Count >= MaxNormalizedItems) break;
                    array.Add(NormalizeValue(item, depth + 1));
                }
                return AutoGameValue.FromArray(array);
            }

            var result = NewObject();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (var index = 0; index < properties.Length && result.Count < MaxNormalizedItems; index++)
            {
                var property = properties[index];
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                try { result[property.Name] = NormalizeValue(property.GetValue(value, null), depth + 1); }
                catch { result[property.Name] = AutoGameValue.FromString("<unavailable>"); }
            }
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (var index = 0; index < fields.Length && result.Count < MaxNormalizedItems; index++)
            {
                var field = fields[index];
                if (result.ContainsKey(field.Name)) continue;
                try { result[field.Name] = NormalizeValue(field.GetValue(value), depth + 1); }
                catch { result[field.Name] = AutoGameValue.FromString("<unavailable>"); }
            }
            return result.Count == 0 ? AutoGameValue.FromString(value.ToString()) : AutoGameValue.FromObject(result);
        }

        static AutoGameValue NormalizeUnityValue(object value, Type type, int depth)
        {
            var name = type.FullName;
            if (name == "UnityEngine.Vector2" || name == "UnityEngine.Vector2Int") return Members(value, depth, "x", "y");
            if (name == "UnityEngine.Vector3" || name == "UnityEngine.Vector3Int") return Members(value, depth, "x", "y", "z");
            if (name == "UnityEngine.Vector4" || name == "UnityEngine.Quaternion") return Members(value, depth, "x", "y", "z", "w");
            if (name == "UnityEngine.Color" || name == "UnityEngine.Color32") return Members(value, depth, "r", "g", "b", "a");
            if (name == "UnityEngine.Rect" || name == "UnityEngine.RectInt") return Members(value, depth, "x", "y", "width", "height");
            if (name == "UnityEngine.Bounds") return Members(value, depth, "center", "size");
            if (name == "UnityEngine.BoundsInt") return Members(value, depth, "position", "size");
            if (name == "UnityEngine.Ray" || name == "UnityEngine.Ray2D") return Members(value, depth, "origin", "direction");
            if (name == "UnityEngine.Plane") return Members(value, depth, "normal", "distance");
            if (name == "UnityEngine.Matrix4x4")
                return Members(value, depth, "m00", "m01", "m02", "m03", "m10", "m11", "m12", "m13",
                    "m20", "m21", "m22", "m23", "m30", "m31", "m32", "m33");
            if (!IsUnityObject(type)) return null;
            var result = NewObject();
            result["type"] = AutoGameValue.FromString(name);
            result["name"] = NormalizeValue(ReadMember(value, "name"), depth + 1);
            var method = type.GetMethod("GetInstanceID", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            result["instanceId"] = method == null ? AutoGameValue.Null : NormalizeValue(method.Invoke(value, null), depth + 1);
            return AutoGameValue.FromObject(result);
        }

        static bool IsUnityObject(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.FullName == "UnityEngine.Object") return true;
            return false;
        }

        static AutoGameValue Members(object value, int depth, params string[] names)
        {
            var result = NewObject();
            for (var index = 0; index < names.Length; index++)
                result[names[index]] = NormalizeValue(ReadMember(value, names[index]), depth + 1);
            return AutoGameValue.FromObject(result);
        }

        static object ReadMember(object value, string name)
        {
            var type = value.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(value, null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(value);
        }

        static Dictionary<string, AutoGameValue> NewObject()
        {
            return new Dictionary<string, AutoGameValue>(StringComparer.Ordinal);
        }
    }
}
