using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace CloudLight.Presence.Xiaomi.Probe;

internal sealed class XiaomiMipsProbe
{
    private readonly string _brokerHost;
    private readonly string _clientId;
    private readonly string _accessToken;
    private readonly string _did;
    private readonly int _siid;
    private readonly IReadOnlyList<int> _connectEventIids;
    private readonly IReadOnlyList<int> _disconnectEventIids;
    private readonly int _clientIdsPiid;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public XiaomiMipsProbe(
        string region,
        string clientUuid,
        string accessToken,
        string did,
        int siid,
        IReadOnlyList<int> connectEventIids,
        IReadOnlyList<int> disconnectEventIids,
        int clientIdsPiid)
    {
        _brokerHost = $"{region}-ha.mqtt.io.mi.com";
        _clientId = $"ha.{clientUuid}";
        _accessToken = accessToken;
        _did = did;
        _siid = siid;
        _connectEventIids = connectEventIids;
        _disconnectEventIids = disconnectEventIids;
        _clientIdsPiid = clientIdsPiid;
    }

    public async Task ObserveEventsAsync(CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_brokerHost, 8883, cancellationToken);
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _brokerHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, cancellationToken);

        await WritePacketAsync(tls, BuildConnectPacket(), cancellationToken);
        var connAck = await ReadPacketAsync(tls, cancellationToken);
        if (connAck.Type != 2 || connAck.Body.Length < 2 || connAck.Body[1] != 0)
        {
            var reason = connAck.Body.Length >= 2 ? $"0x{connAck.Body[1]:X2}" : "missing";
            throw new InvalidOperationException($"MIPS MQTT connection rejected; reason={reason}.");
        }

        var topicFilter = $"device/{_did}/up/event_occured/#";
        await WritePacketAsync(tls, BuildSubscribePacket(1, [topicFilter]), cancellationToken);
        await AwaitSubscribeAckAsync(tls, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"MIPS/MQTT: connected to {_brokerHost}:8883 with TLS and MQTT v5");
        Console.WriteLine($"Subscribed: {topicFilter}");
        var hasSpecEventMappings = _connectEventIids.Count > 0 && _disconnectEventIids.Count > 0;
        if (hasSpecEventMappings)
        {
            Console.WriteLine(
                $"Spec event map: device-connect=[{string.Join(',', _connectEventIids)}], " +
                $"device-disconnect=[{string.Join(',', _disconnectEventIids)}]");
        }
        else
        {
            Console.WriteLine("Actual spec declares no device-connect/device-disconnect events; incoming events will remain unmapped.");
        }
        Console.WriteLine();
        Console.WriteLine("REAL EVENT TEST REQUIRED NOW:");
        Console.WriteLine("1. Connect one device to the AX3000T Wi-Fi.");
        Console.WriteLine("2. Then disconnect that device from Wi-Fi.");
        Console.WriteLine(hasSpecEventMappings
            ? "The probe will stop after both event types are observed or when the 30-minute run expires."
            : "The probe will remain on the wildcard subscription until manually stopped or the 30-minute run expires.");

        using var pingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pingTask = SendPingsAsync(tls, pingCancellation.Token);
        var connectObserved = false;
        var disconnectObserved = false;
        try
        {
            while (!hasSpecEventMappings || !connectObserved || !disconnectObserved)
            {
                var packet = await ReadPacketAsync(tls, cancellationToken);
                switch (packet.Type)
                {
                    case 3:
                        var observed = await HandlePublishAsync(tls, packet, cancellationToken);
                        connectObserved |= observed == "device-connect";
                        disconnectObserved |= observed == "device-disconnect";
                        break;
                    case 6:
                        await HandlePublishReleaseAsync(tls, packet, cancellationToken);
                        break;
                    case 13:
                        break;
                    case 14:
                        var reason = packet.Body.Length > 0 ? $"0x{packet.Body[0]:X2}" : "0x00";
                        throw new IOException($"MIPS MQTT server disconnected; reason={reason}.");
                }
            }

            Console.WriteLine("MIPS event validation complete: both device-connect and device-disconnect were observed.");
        }
        finally
        {
            pingCancellation.Cancel();
            try
            {
                await pingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task AwaitSubscribeAckAsync(SslStream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await ReadPacketAsync(stream, cancellationToken);
            if (packet.Type == 9)
            {
                var offset = 2;
                var propertyLength = ReadVariableByteInteger(packet.Body, ref offset);
                offset += propertyLength;
                var reasons = packet.Body.Skip(offset).ToArray();
                if (reasons.Length == 0 || reasons.Any(reason => reason is not (0 or 1 or 2)))
                {
                    throw new InvalidOperationException(
                        $"MIPS MQTT subscription rejected; reasons={Convert.ToHexString(reasons)}.");
                }

                return;
            }

            if (packet.Type == 3)
            {
                await HandlePublishAsync(stream, packet, cancellationToken);
            }
        }
    }

    private async Task<string?> HandlePublishAsync(
        SslStream stream,
        MqttPacket packet,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        var topic = ReadMqttString(packet.Body, ref offset);
        var qos = (packet.Flags >> 1) & 0x03;
        ushort? packetId = null;
        if (qos > 0)
        {
            packetId = ReadUInt16(packet.Body, ref offset);
        }

        var propertyLength = ReadVariableByteInteger(packet.Body, ref offset);
        offset += propertyLength;
        if (offset > packet.Body.Length)
        {
            throw new InvalidDataException("Invalid MQTT PUBLISH property length.");
        }

        var payload = Encoding.UTF8.GetString(packet.Body, offset, packet.Body.Length - offset);
        if (packetId.HasValue)
        {
            await WritePublishAckAsync(stream, packetId.Value, qos, cancellationToken);
        }

        var eventType = ResolveEventType(payload);
        PrintEventSummary(eventType ?? "unmapped-event", topic, payload, packet.Flags);
        return eventType;
    }

    private string? ResolveEventType(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("params", out var parameters) ||
                !parameters.TryGetProperty("siid", out var siid) ||
                !parameters.TryGetProperty("eiid", out var eiid) ||
                siid.GetInt32() != _siid)
            {
                return null;
            }

            var eventIid = eiid.GetInt32();
            if (_connectEventIids.Contains(eventIid))
            {
                return "device-connect";
            }
            if (_disconnectEventIids.Contains(eventIid))
            {
                return "device-disconnect";
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private void PrintEventSummary(string eventType, string topic, string payload, byte flags)
    {
        var siid = _siid;
        int? eiid = null;
        string? clientIdentifier = null;
        string structure;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("params", out var parameters))
            {
                if (parameters.TryGetProperty("siid", out var siidValue))
                {
                    siid = siidValue.GetInt32();
                }
                if (parameters.TryGetProperty("eiid", out var eiidValue))
                {
                    eiid = eiidValue.GetInt32();
                }
                if (parameters.TryGetProperty("arguments", out var arguments) &&
                    arguments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var argument in arguments.EnumerateArray())
                    {
                        if (argument.TryGetProperty("piid", out var piid) &&
                            piid.GetInt32() == _clientIdsPiid &&
                            argument.TryGetProperty("value", out var value))
                        {
                            clientIdentifier = value.ValueKind == JsonValueKind.String
                                ? value.GetString()
                                : value.GetRawText();
                        }
                    }
                }
            }

            structure = DescribeStructure(root);
        }
        catch (JsonException)
        {
            structure = "non-JSON UTF-8 payload";
        }

        Console.WriteLine();
        Console.WriteLine("MIPS EVENT RECEIVED");
        Console.WriteLine($"  eventType={eventType}");
        Console.WriteLine($"  did={_did}");
        Console.WriteLine($"  siid={siid}");
        Console.WriteLine($"  eiid={(eiid?.ToString() ?? "not-in-payload")}");
        Console.WriteLine($"  observedAtUtc={DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"  clientIdentifier={clientIdentifier ?? "not-obtained"}");
        Console.WriteLine($"  duplicate={((flags & 0x08) != 0)}");
        Console.WriteLine($"  topic={topic}");
        Console.WriteLine($"  payloadStructure={structure}");
        Console.WriteLine($"  rawPayload={payload}");
    }

    private async Task HandlePublishReleaseAsync(
        SslStream stream,
        MqttPacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Body.Length < 2)
        {
            throw new InvalidDataException("Invalid MQTT PUBREL packet.");
        }

        var packetId = BinaryPrimitives.ReadUInt16BigEndian(packet.Body);
        await WritePacketAsync(
            stream,
            new byte[] { 0x70, 0x02, (byte)(packetId >> 8), (byte)packetId },
            cancellationToken);
    }

    private async Task WritePublishAckAsync(
        SslStream stream,
        ushort packetId,
        int qos,
        CancellationToken cancellationToken)
    {
        var packetType = qos == 1 ? (byte)0x40 : (byte)0x50;
        await WritePacketAsync(
            stream,
            new byte[] { packetType, 0x02, (byte)(packetId >> 8), (byte)packetId },
            cancellationToken);
    }

    private async Task SendPingsAsync(SslStream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            await WritePacketAsync(stream, new byte[] { 0xC0, 0x00 }, cancellationToken);
        }
    }

    private byte[] BuildConnectPacket()
    {
        using var body = new MemoryStream();
        WriteMqttString(body, "MQTT");
        body.WriteByte(5);
        body.WriteByte(0xC2);
        WriteUInt16(body, 60);
        body.WriteByte(0);
        WriteMqttString(body, _clientId);
        WriteMqttString(body, XiaomiEndpoints.OAuthClientId);
        WriteMqttBinary(body, Encoding.UTF8.GetBytes(_accessToken));
        return BuildPacket(0x10, body.ToArray());
    }

    private static byte[] BuildSubscribePacket(ushort packetId, IEnumerable<string> topics)
    {
        using var body = new MemoryStream();
        WriteUInt16(body, packetId);
        body.WriteByte(0);
        foreach (var topic in topics)
        {
            WriteMqttString(body, topic);
            body.WriteByte(0x02);
        }
        return BuildPacket(0x82, body.ToArray());
    }

    private async Task WritePacketAsync(
        Stream stream,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(packet, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<MqttPacket> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[1];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var remainingLength = await ReadVariableByteIntegerAsync(stream, cancellationToken);
        var body = new byte[remainingLength];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return new MqttPacket((byte)(header[0] >> 4), (byte)(header[0] & 0x0F), body);
    }

    private static async Task<int> ReadVariableByteIntegerAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var value = 0;
        var multiplier = 1;
        var buffer = new byte[1];
        for (var index = 0; index < 4; index++)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            value += (buffer[0] & 0x7F) * multiplier;
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
            multiplier *= 128;
        }

        throw new InvalidDataException("Invalid MQTT variable-byte integer.");
    }

    private static int ReadVariableByteInteger(byte[] buffer, ref int offset)
    {
        var value = 0;
        var multiplier = 1;
        for (var index = 0; index < 4 && offset < buffer.Length; index++)
        {
            var current = buffer[offset++];
            value += (current & 0x7F) * multiplier;
            if ((current & 0x80) == 0)
            {
                return value;
            }
            multiplier *= 128;
        }

        throw new InvalidDataException("Invalid MQTT variable-byte integer.");
    }

    private static string ReadMqttString(byte[] buffer, ref int offset)
    {
        var length = ReadUInt16(buffer, ref offset);
        if (offset + length > buffer.Length)
        {
            throw new InvalidDataException("Invalid MQTT string length.");
        }
        var value = Encoding.UTF8.GetString(buffer, offset, length);
        offset += length;
        return value;
    }

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        if (offset + 2 > buffer.Length)
        {
            throw new InvalidDataException("Invalid MQTT two-byte integer.");
        }
        var value = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    private static byte[] BuildPacket(byte header, byte[] body)
    {
        using var packet = new MemoryStream();
        packet.WriteByte(header);
        WriteVariableByteInteger(packet, body.Length);
        packet.Write(body);
        return packet.ToArray();
    }

    private static void WriteVariableByteInteger(Stream stream, int value)
    {
        do
        {
            var encoded = (byte)(value % 128);
            value /= 128;
            if (value > 0)
            {
                encoded |= 0x80;
            }
            stream.WriteByte(encoded);
        }
        while (value > 0);
    }

    private static void WriteMqttString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteMqttBinary(Stream stream, byte[] value)
    {
        WriteUInt16(stream, checked((ushort)value.Length));
        stream.Write(value);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string DescribeStructure(JsonElement root)
    {
        var parts = new List<string>();
        Visit(root, "$", parts);
        return string.Join(", ", parts);

        static void Visit(JsonElement element, string path, List<string> destination)
        {
            destination.Add($"{path}:{element.ValueKind}");
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value, $"{path}.{property.Name}", destination);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray().Take(1))
                {
                    Visit(item, $"{path}[]", destination);
                }
            }
        }
    }

    private sealed record MqttPacket(byte Type, byte Flags, byte[] Body);
}
