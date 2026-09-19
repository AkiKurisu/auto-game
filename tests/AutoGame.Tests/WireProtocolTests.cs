using System.Text;
using AutoGame;
using Xunit;

namespace AutoGame.Tests;

public sealed class WireProtocolTests
{
    [Fact]
    public void RoundTripsEveryValueKind()
    {
        var value = AutoGameValue.FromObject(new Dictionary<string, AutoGameValue>
        {
            ["null"] = AutoGameValue.Null,
            ["false"] = AutoGameValue.FromBoolean(false),
            ["true"] = AutoGameValue.FromBoolean(true),
            ["min"] = AutoGameValue.FromInt64(long.MinValue),
            ["max"] = AutoGameValue.FromUInt64(ulong.MaxValue),
            ["number"] = AutoGameValue.FromDouble(123.5),
            ["text"] = AutoGameValue.FromString("你好 🎮"),
            ["array"] = AutoGameValue.FromArray(new[] { AutoGameValue.FromInt64(1), AutoGameValue.FromString("two") })
        });

        var decoded = WireProtocol.Decode(WireProtocol.Encode(value)).AsObject();

        Assert.Equal(AutoGameValueKind.Null, decoded["null"].Kind);
        Assert.False(decoded["false"].AsBoolean());
        Assert.True(decoded["true"].AsBoolean());
        Assert.Equal(long.MinValue, decoded["min"].AsInt64());
        Assert.Equal(ulong.MaxValue, decoded["max"].AsUInt64());
        Assert.Equal(123.5, decoded["number"].AsDouble());
        Assert.Equal("你好 🎮", decoded["text"].AsString());
        Assert.Equal(2, decoded["array"].AsArray().Count);
    }

    [Fact]
    public void JsonProjectionPreservesSupportedScalarKinds()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"signed\":-9223372036854775808,\"unsigned\":18446744073709551615,\"number\":1.25,\"text\":\"测试\",\"items\":[true,null]}");

        var projected = WireJson.ToJson(WireJson.FromJson(json))!.AsObject();

        Assert.Equal(long.MinValue, projected["signed"]!.GetValue<long>());
        Assert.Equal(ulong.MaxValue, projected["unsigned"]!.GetValue<ulong>());
        Assert.Equal(1.25, projected["number"]!.GetValue<double>());
        Assert.Equal("测试", projected["text"]!.GetValue<string>());
        Assert.True(projected["items"]![0]!.GetValue<bool>());
        Assert.Null(projected["items"]![1]);
    }

    [Fact]
    public void AutoGameArgsProvidesTypedAccess()
    {
        var args = new AutoGameArgs(AutoGameValue.FromObject(new Dictionary<string, AutoGameValue>
        {
            ["name"] = AutoGameValue.FromString("player"),
            ["enabled"] = AutoGameValue.FromBoolean(true),
            ["count"] = AutoGameValue.FromInt64(3),
            ["ratio"] = AutoGameValue.FromDouble(0.5)
        }));

        Assert.Equal("player", args.GetString("name"));
        Assert.True(args.GetBoolean("enabled"));
        Assert.Equal(3, args.GetInt64("count"));
        Assert.Equal(0.5, args.GetDouble("ratio"));
        Assert.True(args.TryGetValue("count", out var count));
        Assert.Equal(3, count.AsInt64());
    }

    [Fact]
    public void ReadsStructuredBridgeFailureWithoutJsonFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), "auto-game-error-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, WireProtocol.Encode(AutoGameValue.FromObject(new Dictionary<string, AutoGameValue>
            {
                ["code"] = AutoGameValue.FromString("UnityBridgePumpFailed"),
                ["message"] = AutoGameValue.FromString("pump failed")
            })));

            var error = BridgeClient.ReadBridgeFailure(path);

            Assert.Equal("UnityBridgePumpFailed", error.Code);
            Assert.Equal("pump failed", error.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadsBinaryConnectionIntegerFieldsAsInt64()
    {
        var path = Path.Combine(Path.GetTempPath(), "auto-game-connection-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, WireProtocol.Encode(AutoGameValue.FromObject(new Dictionary<string, AutoGameValue>
            {
                ["protocol"] = AutoGameValue.FromInt64(AttachProtocol.Version),
                ["generation"] = AutoGameValue.FromString("generation")
            })));

            Assert.Equal("generation", BridgeClient.ReadGeneration(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FrameRoundTripsAndRejectsTruncation()
    {
        using var stream = new MemoryStream();
        WireProtocol.WriteFrame(stream, AutoGameValue.FromString("frame"));
        stream.Position = 0;
        Assert.Equal("frame", WireProtocol.ReadFrame(stream).AsString());

        var truncated = stream.ToArray()[..^1];
        Assert.Throws<EndOfStreamException>(() => WireProtocol.ReadFrame(new MemoryStream(truncated)));
    }

    [Fact]
    public void RejectsMalformedAndOversizedValues()
    {
        Assert.Throws<InvalidDataException>(() => WireProtocol.Decode([]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.Decode([255]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.Decode([(byte)AutoGameValueKind.Boolean, 2]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.Decode([0, 0]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.Decode(new byte[WireProtocol.MaxMessageBytes + 1]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.Encode(AutoGameValue.FromString(
            new string('x', WireProtocol.MaxStringBytes + 1))));
        Assert.Throws<InvalidDataException>(() => WireProtocol.Encode(AutoGameValue.FromArray(
            Enumerable.Repeat(AutoGameValue.Null, WireProtocol.MaxCollectionLength + 1).ToArray())));
    }

    [Fact]
    public void RejectsValuesBeyondDepthLimit()
    {
        var value = AutoGameValue.Null;
        for (var index = 0; index < WireProtocol.MaxDepth + 2; index++)
            value = AutoGameValue.FromArray(new[] { value });
        Assert.Throws<InvalidDataException>(() => WireProtocol.Encode(value));
    }

    [Fact]
    public void RandomMalformedInputsFailWithinProtocolBoundary()
    {
        var random = new Random(9173);
        for (var index = 0; index < 500; index++)
        {
            var bytes = new byte[random.Next(1, 128)];
            random.NextBytes(bytes);
            try { _ = WireProtocol.Decode(bytes); }
            catch (Exception error)
            {
                Assert.True(error is InvalidDataException or IOException or DecoderFallbackException or ArgumentException,
                    "Unexpected exception: " + error.GetType().FullName);
            }
        }
    }
}
