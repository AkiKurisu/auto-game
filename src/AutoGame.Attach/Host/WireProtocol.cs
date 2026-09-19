#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoGame
{
    internal static class WireProtocol
    {
        internal const int MaxMessageBytes = 4 * 1024 * 1024;
        internal const int MaxDepth = 16;
        internal const int MaxCollectionLength = 4096;
        internal const int MaxStringBytes = 1024 * 1024;
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(AutoGameValue value)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                WriteValue(writer, value ?? AutoGameValue.Null, 0);
                writer.Flush();
                if (stream.Length > MaxMessageBytes) throw new InvalidDataException("Wire message exceeds the size limit.");
                return stream.ToArray();
            }
        }

        internal static AutoGameValue Decode(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxMessageBytes)
                throw new InvalidDataException("Invalid wire message size.");
            using (var stream = new MemoryStream(data, false))
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                var value = ReadValue(reader, 0);
                if (stream.Position != stream.Length) throw new InvalidDataException("Wire message has trailing data.");
                return value;
            }
        }

        internal static void WriteFrame(Stream stream, AutoGameValue value)
        {
            var payload = Encode(value);
            var header = BitConverter.GetBytes(payload.Length);
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        internal static AutoGameValue ReadFrame(Stream stream)
        {
            var header = ReadExactly(stream, sizeof(int));
            var length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > MaxMessageBytes) throw new InvalidDataException("Invalid wire frame size.");
            return Decode(ReadExactly(stream, length));
        }

        static byte[] ReadExactly(Stream stream, int length)
        {
            var bytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(bytes, offset, length - offset);
                if (read == 0) throw new EndOfStreamException("Wire frame ended unexpectedly.");
                offset += read;
            }
            return bytes;
        }

        static void WriteValue(BinaryWriter writer, AutoGameValue value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Wire value exceeds the depth limit.");
            writer.Write((byte)value.Kind);
            switch (value.Kind)
            {
                case AutoGameValueKind.Null: return;
                case AutoGameValueKind.Boolean: writer.Write(value.AsBoolean()); return;
                case AutoGameValueKind.Int64: writer.Write(value.AsInt64()); return;
                case AutoGameValueKind.UInt64: writer.Write(value.AsUInt64()); return;
                case AutoGameValueKind.Double: writer.Write(value.AsDouble()); return;
                case AutoGameValueKind.String: WriteString(writer, value.AsString()); return;
                case AutoGameValueKind.Array:
                    var array = value.AsArray();
                    RequireCollectionLength(array.Count);
                    writer.Write(array.Count);
                    for (var index = 0; index < array.Count; index++) WriteValue(writer, array[index] ?? AutoGameValue.Null, depth + 1);
                    return;
                case AutoGameValueKind.Object:
                    var map = value.AsObject();
                    RequireCollectionLength(map.Count);
                    writer.Write(map.Count);
                    foreach (var pair in map)
                    {
                        WriteString(writer, pair.Key);
                        WriteValue(writer, pair.Value ?? AutoGameValue.Null, depth + 1);
                    }
                    return;
                default: throw new InvalidDataException("Unknown wire value kind.");
            }
        }

        static AutoGameValue ReadValue(BinaryReader reader, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Wire value exceeds the depth limit.");
            var kind = (AutoGameValueKind)reader.ReadByte();
            switch (kind)
            {
                case AutoGameValueKind.Null: return AutoGameValue.Null;
                case AutoGameValueKind.Boolean:
                    var boolean = reader.ReadByte();
                    if (boolean > 1) throw new InvalidDataException("Invalid wire Boolean value.");
                    return AutoGameValue.FromBoolean(boolean == 1);
                case AutoGameValueKind.Int64: return AutoGameValue.FromInt64(reader.ReadInt64());
                case AutoGameValueKind.UInt64: return AutoGameValue.FromUInt64(reader.ReadUInt64());
                case AutoGameValueKind.Double: return AutoGameValue.FromDouble(reader.ReadDouble());
                case AutoGameValueKind.String: return AutoGameValue.FromString(ReadString(reader));
                case AutoGameValueKind.Array:
                    var arrayCount = ReadCollectionLength(reader);
                    var array = new List<AutoGameValue>(arrayCount);
                    for (var index = 0; index < arrayCount; index++) array.Add(ReadValue(reader, depth + 1));
                    return AutoGameValue.FromArray(array);
                case AutoGameValueKind.Object:
                    var objectCount = ReadCollectionLength(reader);
                    var map = new Dictionary<string, AutoGameValue>(objectCount, StringComparer.Ordinal);
                    for (var index = 0; index < objectCount; index++)
                    {
                        var key = ReadString(reader);
                        if (map.ContainsKey(key)) throw new InvalidDataException("Wire object contains a duplicate key.");
                        map.Add(key, ReadValue(reader, depth + 1));
                    }
                    return AutoGameValue.FromObject(map);
                default: throw new InvalidDataException("Unknown wire value kind: " + (byte)kind + ".");
            }
        }

        static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null) throw new InvalidDataException("Wire strings cannot be null.");
            var bytes = Utf8.GetBytes(value);
            if (bytes.Length > MaxStringBytes) throw new InvalidDataException("Wire string exceeds the size limit.");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > MaxStringBytes) throw new InvalidDataException("Invalid wire string size.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException("Wire string ended unexpectedly.");
            return Utf8.GetString(bytes);
        }

        static int ReadCollectionLength(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            RequireCollectionLength(length);
            return length;
        }

        static void RequireCollectionLength(int length)
        {
            if (length < 0 || length > MaxCollectionLength) throw new InvalidDataException("Invalid wire collection size.");
        }
    }
}
