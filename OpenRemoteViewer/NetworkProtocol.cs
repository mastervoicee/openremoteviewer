using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenRemoteViewer;

public enum MessageType : byte
{
    AuthOk = 2,
    AuthFail = 3,
    MonitorList = 4,
    ScreenFrame = 5,
    Click = 6,
    SwitchMonitor = 7,
    Disconnect = 8,
    CursorMonitor = 9,
    Draw = 10,
    CaptureBlocked = 11
}

/// <summary>
/// One point of a freehand stroke drawn by the admin on the client's screen.
/// Coordinates are pixels of the currently shared monitor.
/// </summary>
public readonly record struct DrawPoint(int StrokeId, bool IsStart, int X, int Y)
{
    public const int PayloadSize = 13;

    public byte[] ToPayload()
    {
        var buf = new byte[PayloadSize];
        BitConverter.TryWriteBytes(buf.AsSpan(0), StrokeId);
        buf[4] = IsStart ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(buf.AsSpan(5), X);
        BitConverter.TryWriteBytes(buf.AsSpan(9), Y);
        return buf;
    }

    public static bool TryParse(byte[] payload, out DrawPoint point)
    {
        point = default;
        if (payload.Length < PayloadSize) return false;
        point = new DrawPoint(
            BitConverter.ToInt32(payload, 0),
            payload[4] != 0,
            BitConverter.ToInt32(payload, 5),
            BitConverter.ToInt32(payload, 9));
        return true;
    }
}

public sealed class SecureConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private SecureConnection(TcpClient tcp, byte[] key)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _tcp.SendBufferSize = 512 * 1024;
        _tcp.ReceiveBufferSize = 512 * 1024;
        _stream = tcp.GetStream();
        _key = key;
    }

    /// <summary>
    /// Client-side: accept connection, perform challenge-response authentication.
    /// Returns null if authentication fails.
    /// </summary>
    public static async Task<SecureConnection?> AcceptAsync(
        TcpListener listener, string password, CancellationToken ct)
    {
        var tcp = await listener.AcceptTcpClientAsync(ct);
        var stream = tcp.GetStream();

        byte[] salt = new byte[16];
        byte[] challenge = new byte[16];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(challenge);

        var handshake = new byte[32];
        Buffer.BlockCopy(salt, 0, handshake, 0, 16);
        Buffer.BlockCopy(challenge, 0, handshake, 16, 16);
        await stream.WriteAsync(handshake, ct);

        byte[] key = DeriveKey(password, salt);

        var response = new byte[44]; // 12 nonce + 16 ciphertext + 16 tag
        await ReadExactAsync(stream, response, ct);

        try
        {
            byte[] decrypted = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(
                response.AsSpan(0, 12),
                response.AsSpan(12, 16),
                response.AsSpan(28, 16),
                decrypted);

            if (!CryptographicOperations.FixedTimeEquals(decrypted, challenge))
            {
                tcp.Close();
                return null;
            }
        }
        catch
        {
            tcp.Close();
            return null;
        }

        var conn = new SecureConnection(tcp, key);
        await conn.SendMessageAsync(MessageType.AuthOk, [], ct);
        return conn;
    }

    /// <summary>
    /// Admin-side: connect to client, perform challenge-response authentication.
    /// Returns null if authentication fails.
    /// </summary>
    public static async Task<SecureConnection?> ConnectAsync(
        string host, int port, string password, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
        }
        catch
        {
            tcp.Close();
            throw;
        }

        var stream = tcp.GetStream();

        var handshake = new byte[32];
        await ReadExactAsync(stream, handshake, ct);
        byte[] salt = handshake[..16];
        byte[] challenge = handshake[16..];

        byte[] key = DeriveKey(password, salt);

        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] ciphertext = new byte[16];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, challenge, ciphertext, tag);
        }

        var response = new byte[44];
        Buffer.BlockCopy(nonce, 0, response, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, response, 12, 16);
        Buffer.BlockCopy(tag, 0, response, 28, 16);
        await stream.WriteAsync(response, ct);

        var conn = new SecureConnection(tcp, key);
        var (type, _) = await conn.ReceiveMessageAsync(ct);
        if (type != MessageType.AuthOk)
        {
            conn.Dispose();
            return null;
        }

        return conn;
    }

    public async Task SendMessageAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] ciphertext = new byte[payload.Length];
        byte[] tag = new byte[16];

        using (var aes = new AesGcm(_key, 16))
        {
            aes.Encrypt(nonce, payload, ciphertext, tag);
        }

        int blockLen = 12 + ciphertext.Length + 16;
        var packet = new byte[5 + blockLen];
        packet[0] = (byte)type;
        BitConverter.TryWriteBytes(packet.AsSpan(1), blockLen);
        Buffer.BlockCopy(nonce, 0, packet, 5, 12);
        Buffer.BlockCopy(ciphertext, 0, packet, 17, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packet, 17 + ciphertext.Length, 16);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(packet, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(MessageType type, byte[] payload)> ReceiveMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await ReadExactAsync(_stream, header, ct);

        var type = (MessageType)header[0];
        int blockLen = BitConverter.ToInt32(header, 1);

        if (blockLen < 28 || blockLen > 20_000_000)
            throw new InvalidDataException($"Invalid block length: {blockLen}");

        var block = new byte[blockLen];
        await ReadExactAsync(_stream, block, ct);

        int ciphertextLen = blockLen - 28;
        byte[] plaintext = new byte[ciphertextLen];

        using (var aes = new AesGcm(_key, 16))
        {
            aes.Decrypt(
                block.AsSpan(0, 12),
                block.AsSpan(12, ciphertextLen),
                block.AsSpan(blockLen - 16, 16),
                plaintext);
        }

        return (type, plaintext);
    }

    public bool IsConnected => _tcp.Connected && !_disposed;

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new IOException("Connection closed by remote side");
            offset += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        try { _stream.Close(); } catch { }
        try { _tcp.Close(); } catch { }
    }
}
