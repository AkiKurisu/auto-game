#nullable disable
using System;
using System.Collections.Generic;

namespace AutoGame
{
    internal static class WireMap
    {
        internal static AutoGameValue Object(params object[] entries)
        {
            var result = new Dictionary<string, AutoGameValue>(StringComparer.Ordinal);
            for (var index = 0; index < entries.Length; index += 2)
                result[(string)entries[index]] = Value(entries[index + 1]);
            return AutoGameValue.FromObject(result);
        }

        internal static AutoGameValue Array(IEnumerable<string> values)
        {
            var result = new List<AutoGameValue>();
            foreach (var value in values) result.Add(AutoGameValue.FromString(value));
            return AutoGameValue.FromArray(result);
        }

        internal static AutoGameValue Value(object value)
        {
            if (value == null) return AutoGameValue.Null;
            if (value is AutoGameValue) return (AutoGameValue)value;
            return UnityValueNormalizer.Normalize(value);
        }

        internal static AutoGameValue Get(IDictionary<string, AutoGameValue> map, string key)
        {
            AutoGameValue value;
            return map.TryGetValue(key, out value) ? value : AutoGameValue.Null;
        }

        internal static string String(IDictionary<string, AutoGameValue> map, string key)
        {
            var value = Get(map, key);
            return value.Kind == AutoGameValueKind.Null ? null : value.AsString();
        }

        internal static long Int64(IDictionary<string, AutoGameValue> map, string key, long fallback)
        {
            var value = Get(map, key);
            if (value.Kind == AutoGameValueKind.Null) return fallback;
            if (value.Kind == AutoGameValueKind.UInt64) return checked((long)value.AsUInt64());
            return value.AsInt64();
        }

        internal static bool Boolean(IDictionary<string, AutoGameValue> map, string key)
        {
            var value = Get(map, key);
            return value.Kind != AutoGameValueKind.Null && value.AsBoolean();
        }
    }
}
