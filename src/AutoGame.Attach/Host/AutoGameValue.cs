#nullable disable
using System;
using System.Collections.Generic;

namespace AutoGame
{
    public enum AutoGameValueKind : byte
    {
        Null,
        Boolean,
        Int64,
        UInt64,
        Double,
        String,
        Array,
        Object
    }

    public sealed class AutoGameValue
    {
        readonly object value;

        AutoGameValue(AutoGameValueKind kind, object value)
        {
            Kind = kind;
            this.value = value;
        }

        public static readonly AutoGameValue Null = new AutoGameValue(AutoGameValueKind.Null, null);
        public AutoGameValueKind Kind { get; private set; }
        public static AutoGameValue FromBoolean(bool value) { return new AutoGameValue(AutoGameValueKind.Boolean, value); }
        public static AutoGameValue FromInt64(long value) { return new AutoGameValue(AutoGameValueKind.Int64, value); }
        public static AutoGameValue FromUInt64(ulong value) { return new AutoGameValue(AutoGameValueKind.UInt64, value); }
        public static AutoGameValue FromDouble(double value) { return new AutoGameValue(AutoGameValueKind.Double, value); }
        public static AutoGameValue FromString(string value) { return value == null ? Null : new AutoGameValue(AutoGameValueKind.String, value); }
        public static AutoGameValue FromArray(IList<AutoGameValue> value) { return new AutoGameValue(AutoGameValueKind.Array, value ?? throw new ArgumentNullException("value")); }
        public static AutoGameValue FromObject(IDictionary<string, AutoGameValue> value) { return new AutoGameValue(AutoGameValueKind.Object, value ?? throw new ArgumentNullException("value")); }

        public bool AsBoolean() { return Require<bool>(AutoGameValueKind.Boolean); }
        public long AsInt64() { return Require<long>(AutoGameValueKind.Int64); }
        public ulong AsUInt64() { return Require<ulong>(AutoGameValueKind.UInt64); }
        public double AsDouble()
        {
            if (Kind == AutoGameValueKind.Int64) return (long)value;
            if (Kind == AutoGameValueKind.UInt64) return (ulong)value;
            return Require<double>(AutoGameValueKind.Double);
        }
        public string AsString() { return Require<string>(AutoGameValueKind.String); }
        public IList<AutoGameValue> AsArray() { return Require<IList<AutoGameValue>>(AutoGameValueKind.Array); }
        public IDictionary<string, AutoGameValue> AsObject() { return Require<IDictionary<string, AutoGameValue>>(AutoGameValueKind.Object); }

        T Require<T>(AutoGameValueKind expected)
        {
            if (Kind != expected) throw new InvalidOperationException("Expected " + expected + " but found " + Kind + ".");
            return (T)value;
        }
    }

    public sealed class AutoGameArgs
    {
        readonly IDictionary<string, AutoGameValue> values;

        internal AutoGameArgs(AutoGameValue value)
        {
            values = value == null || value.Kind == AutoGameValueKind.Null
                ? new Dictionary<string, AutoGameValue>(StringComparer.Ordinal)
                : value.AsObject();
        }

        public AutoGameValue this[string key] { get { return values[key]; } }
        public int Count { get { return values.Count; } }
        public bool ContainsKey(string key) { return values.ContainsKey(key); }
        public bool TryGetValue(string key, out AutoGameValue value) { return values.TryGetValue(key, out value); }
        public string GetString(string key) { return this[key].AsString(); }
        public bool GetBoolean(string key) { return this[key].AsBoolean(); }
        public long GetInt64(string key) { return this[key].AsInt64(); }
        public ulong GetUInt64(string key) { return this[key].AsUInt64(); }
        public double GetDouble(string key) { return this[key].AsDouble(); }
        public IList<AutoGameValue> GetArray(string key) { return this[key].AsArray(); }
        public IDictionary<string, AutoGameValue> GetObject(string key) { return this[key].AsObject(); }
    }
}
