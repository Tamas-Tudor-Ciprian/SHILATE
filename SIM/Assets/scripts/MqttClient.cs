using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// Minimal MQTT 3.1.1 client supporting CONNECT, PUBLISH (QoS 0),
/// SUBSCRIBE, PING, and DISCONNECT. Designed for Unity (no external dependencies).
/// </summary>
public sealed class MqttClient : IDisposable
{
    public enum ConnectionState { Disconnected, Connecting, Connected }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event Action OnConnected;
    public event Action<string> OnDisconnected; // reason
    public event Action<string, string> OnMessageReceived; // topic, payload

    readonly string _host;
    readonly int _port;
    readonly string _clientId;
    readonly int _keepAliveSec;

    TcpClient _tcp;
    NetworkStream _stream;
    Thread _readThread;
    volatile bool _running;
    ushort _nextPacketId = 1;
    readonly object _writeLock = new object();
    readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

    DateTime _lastSendTime;
    Timer _pingTimer;

    public MqttClient(string host, int port, string clientId = null, int keepAliveSec = 30)
    {
        _host = host;
        _port = port;
        _clientId = clientId ?? ("unity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        _keepAliveSec = keepAliveSec;
    }

    /// <summary>Call from Unity main thread to dispatch callbacks.</summary>
    public void ProcessMessages()
    {
        while (_mainThreadQueue.TryDequeue(out var action))
            action?.Invoke();
    }

    public void Connect()
    {
        if (State != ConnectionState.Disconnected) return;
        State = ConnectionState.Connecting;

        try
        {
            _tcp = new TcpClient();
            _tcp.Connect(_host, _port);
            _stream = _tcp.GetStream();
            _running = true;

            SendConnectPacket();

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "MQTT-Read" };
            _readThread.Start();

            // Keep-alive ping timer
            int intervalMs = (_keepAliveSec * 1000) / 2;
            _pingTimer = new Timer(_ => SendPing(), null, intervalMs, intervalMs);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            _mainThreadQueue.Enqueue(() => OnDisconnected?.Invoke(ex.Message));
        }
    }

    public void Publish(string topic, string payload)
    {
        if (State != ConnectionState.Connected) return;

        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        int remainingLength = 2 + topicBytes.Length + payloadBytes.Length;

        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x30); // PUBLISH, QoS 0, no retain
            WriteRemainingLength(ms, remainingLength);
            WriteUtf8String(ms, topicBytes);
            ms.Write(payloadBytes, 0, payloadBytes.Length);
            Send(ms.ToArray());
        }
    }

    public void Subscribe(string topic, byte qos = 0)
    {
        if (State != ConnectionState.Connected) return;

        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        int remainingLength = 2 + 2 + topicBytes.Length + 1; // packetId + topic + qos

        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x82); // SUBSCRIBE
            WriteRemainingLength(ms, remainingLength);
            ushort packetId = _nextPacketId++;
            ms.WriteByte((byte)(packetId >> 8));
            ms.WriteByte((byte)(packetId & 0xFF));
            WriteUtf8String(ms, topicBytes);
            ms.WriteByte(qos);
            Send(ms.ToArray());
        }
    }

    public void Disconnect()
    {
        if (State == ConnectionState.Disconnected) return;
        _running = false;

        try
        {
            _pingTimer?.Dispose();
            _pingTimer = null;

            // Send DISCONNECT packet
            Send(new byte[] { 0xE0, 0x00 });
            _stream?.Close();
            _tcp?.Close();
        }
        catch { }

        State = ConnectionState.Disconnected;
    }

    public void Dispose() => Disconnect();

    // ─── Protocol internals ───

    void SendConnectPacket()
    {
        byte[] clientIdBytes = Encoding.UTF8.GetBytes(_clientId);

        using (var ms = new MemoryStream())
        {
            // Variable header
            WriteUtf8String(ms, Encoding.UTF8.GetBytes("MQTT")); // protocol name
            ms.WriteByte(0x04); // protocol level 3.1.1
            ms.WriteByte(0x02); // connect flags: clean session
            ms.WriteByte((byte)(_keepAliveSec >> 8));
            ms.WriteByte((byte)(_keepAliveSec & 0xFF));

            // Payload
            WriteUtf8String(ms, clientIdBytes);

            byte[] variableAndPayload = ms.ToArray();

            using (var packet = new MemoryStream())
            {
                packet.WriteByte(0x10); // CONNECT
                WriteRemainingLength(packet, variableAndPayload.Length);
                packet.Write(variableAndPayload, 0, variableAndPayload.Length);
                Send(packet.ToArray());
            }
        }
    }

    void SendPing()
    {
        if (State != ConnectionState.Connected) return;
        if ((DateTime.UtcNow - _lastSendTime).TotalSeconds < _keepAliveSec / 2) return;

        try { Send(new byte[] { 0xC0, 0x00 }); }
        catch { }
    }

    void Send(byte[] data)
    {
        lock (_writeLock)
        {
            try
            {
                _stream?.Write(data, 0, data.Length);
                _stream?.Flush();
                _lastSendTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                HandleConnectionLost("Send error: " + ex.Message);
            }
        }
    }

    void ReadLoop()
    {
        try
        {
            while (_running && _stream != null)
            {
                if (!_stream.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }

                int packetType = _stream.ReadByte();
                if (packetType < 0) break;

                int remainingLength = ReadRemainingLength();
                byte[] payload = new byte[remainingLength];
                int totalRead = 0;
                while (totalRead < remainingLength)
                {
                    int n = _stream.Read(payload, totalRead, remainingLength - totalRead);
                    if (n <= 0) break;
                    totalRead += n;
                }

                HandlePacket(packetType, payload);
            }
        }
        catch (Exception ex)
        {
            if (_running) HandleConnectionLost("Read error: " + ex.Message);
        }
    }

    void HandlePacket(int packetType, byte[] data)
    {
        int type = packetType & 0xF0;

        switch (type)
        {
            case 0x20: // CONNACK
                if (data.Length >= 2 && data[1] == 0x00)
                {
                    State = ConnectionState.Connected;
                    _mainThreadQueue.Enqueue(() => OnConnected?.Invoke());
                }
                else
                {
                    byte code = data.Length >= 2 ? data[1] : (byte)0xFF;
                    HandleConnectionLost("CONNACK refused, code: " + code);
                }
                break;

            case 0x30: // PUBLISH (incoming)
                ParsePublish(packetType, data);
                break;

            case 0x90: // SUBACK
                break; // acknowledged

            case 0xD0: // PINGRESP
                break;
        }
    }

    void ParsePublish(int header, byte[] data)
    {
        int idx = 0;
        int topicLen = (data[idx] << 8) | data[idx + 1]; idx += 2;
        string topic = Encoding.UTF8.GetString(data, idx, topicLen); idx += topicLen;

        int qos = (header >> 1) & 0x03;
        if (qos > 0)
        {
            // Skip packet identifier for QoS 1/2
            idx += 2;
        }

        string payload = Encoding.UTF8.GetString(data, idx, data.Length - idx);
        _mainThreadQueue.Enqueue(() => OnMessageReceived?.Invoke(topic, payload));
    }

    void HandleConnectionLost(string reason)
    {
        _running = false;
        State = ConnectionState.Disconnected;
        _pingTimer?.Dispose();
        _pingTimer = null;
        _mainThreadQueue.Enqueue(() => OnDisconnected?.Invoke(reason));
    }

    // ─── Encoding helpers ───

    int ReadRemainingLength()
    {
        int value = 0, multiplier = 1;
        byte encoded;
        do
        {
            int b = _stream.ReadByte();
            if (b < 0) return 0;
            encoded = (byte)b;
            value += (encoded & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((encoded & 0x80) != 0);
        return value;
    }

    static void WriteRemainingLength(MemoryStream ms, int length)
    {
        do
        {
            byte encoded = (byte)(length & 0x7F);
            length >>= 7;
            if (length > 0) encoded |= 0x80;
            ms.WriteByte(encoded);
        } while (length > 0);
    }

    static void WriteUtf8String(MemoryStream ms, byte[] str)
    {
        ms.WriteByte((byte)(str.Length >> 8));
        ms.WriteByte((byte)(str.Length & 0xFF));
        ms.Write(str, 0, str.Length);
    }
}
