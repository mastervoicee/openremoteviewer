using System.Buffers.Binary;
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

/// <summary>
/// Slows down password guessing over the network. After a few wrong passwords from
/// one address that address is refused for a growing time — without touching
/// connections from other addresses, so a guesser cannot lock the real admin out.
/// </summary>
internal static class AuthThrottle
{
    private const int FreeAttempts = 5;
    private static readonly TimeSpan MaxBlock = TimeSpan.FromHours(1);
    private static readonly Dictionary<IPAddress, (int Fails, DateTime BlockedUntil)> Entries = [];

    public static bool IsBlocked(IPAddress ip)
    {
        lock (Entries)
            return Entries.TryGetValue(ip, out var e) && e.BlockedUntil > DateTime.UtcNow;
    }

    public static void RecordFailure(IPAddress ip)
    {
        lock (Entries)
        {
            if (Entries.Count > 10_000)
            {
                var now = DateTime.UtcNow;
                foreach (var stale in Entries.Where(p => p.Value.BlockedUntil < now).Select(p => p.Key).ToList())
                    Entries.Remove(stale);
            }

            int fails = Entries.TryGetValue(ip, out var e) ? e.Fails + 1 : 1;
            var until = DateTime.MinValue;
            if (fails >= FreeAttempts)
            {
                // 1, 2, 4, 8 ... минут, но не дольше часа
                double minutes = Math.Pow(2, Math.Min(fails - FreeAttempts, 10));
                var block = TimeSpan.FromMinutes(minutes);
                until = DateTime.UtcNow + (block < MaxBlock ? block : MaxBlock);
            }
            Entries[ip] = (fails, until);
        }
    }

    public static void RecordSuccess(IPAddress ip)
    {
        lock (Entries) Entries.Remove(ip);
    }
}

public sealed class SecureConnection : IDisposable
{
    /// <summary>Shortest password the settings window accepts.</summary>
    public const int MinPasswordLength = 10;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _key;
    private readonly bool _isClientSide;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    // Номера сообщений в каждом направлении. Вместе с типом и направлением входят
    // в проверяемые данные шифра: пакет нельзя повторить, переставить, отразить
    // обратно отправителю или выдать за сообщение другого типа.
    private ulong _sendSeq;
    private ulong _recvSeq;

    // Клиент версии до 1.2.0: без проверяемого заголовка. Включается только на
    // стороне админа и только если так ответил сам клиент — подменить ответ
    // нового клиента на «старый» без пароля нельзя.
    private bool _legacyPeer;

    /// <summary>Сколько ждём рукопожатие. Молчащее подключение не должно занимать порт вечно.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Сколько ждём, пока уйдёт один пакет. Дольше — соединение мертво.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>True when the other side runs a version older than 1.2.0.</summary>
    public bool IsLegacyPeer => _legacyPeer;

    private SecureConnection(TcpClient tcp, byte[] key, bool isClientSide)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        EnableKeepAlive(_tcp.Client);
        _tcp.SendBufferSize = 512 * 1024;
        _tcp.ReceiveBufferSize = 512 * 1024;
        _stream = tcp.GetStream();
        _key = key;
        _isClientSide = isClientSide;
    }

    /// <summary>
    /// Client-side: accept connection, perform challenge-response authentication.
    /// Returns null if authentication fails.
    /// </summary>
    public static async Task<SecureConnection?> AcceptAsync(
        TcpListener listener, string password, CancellationToken ct)
    {
        var tcp = await listener.AcceptTcpClientAsync(ct);

        var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        // Адрес, с которого подбирали пароль, отсекаем сразу — без рукопожатия.
        if (AuthThrottle.IsBlocked(remote))
        {
            tcp.Close();
            return null;
        }

        // Рукопожатие ограничено по времени: чужое подключение, которое молчит
        // (сканер портов, зависший админ), иначе блокировало бы клиента навсегда.
        using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hsCts.CancelAfter(HandshakeTimeout);
        try
        {
            return await AcceptHandshakeAsync(tcp, remote, password, hsCts.Token);
        }
        catch when (!ct.IsCancellationRequested)
        {
            tcp.Close();
            return null;
        }
        catch
        {
            tcp.Close();
            throw;
        }
    }

    private static async Task<SecureConnection?> AcceptHandshakeAsync(
        TcpClient tcp, IPAddress remote, string password, CancellationToken ct)
    {
        var stream = tcp.GetStream();

        byte[] salt = new byte[16];
        byte[] challenge = new byte[16];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(challenge);

        var handshake = new byte[32];
        Buffer.BlockCopy(salt, 0, handshake, 0, 16);
        Buffer.BlockCopy(challenge, 0, handshake, 16, 16);
        await stream.WriteAsync(handshake, ct);

        // вывод ключа тяжёлый — не в потоке интерфейса
        byte[] key = await Task.Run(() => DeriveKey(password, salt), ct);

        var response = new byte[44]; // 12 nonce + 16 ciphertext + 16 tag
        await ReadExactAsync(stream, response, ct);

        bool ok;
        try
        {
            byte[] decrypted = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(
                response.AsSpan(0, 12),
                response.AsSpan(12, 16),
                response.AsSpan(28, 16),
                decrypted);

            ok = CryptographicOperations.FixedTimeEquals(decrypted, challenge);
        }
        catch (CryptographicException)
        {
            ok = false;
        }

        if (!ok)
        {
            AuthThrottle.RecordFailure(remote);
            tcp.Close();
            return null;
        }

        AuthThrottle.RecordSuccess(remote);

        var conn = new SecureConnection(tcp, key, isClientSide: true);
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
        using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hsCts.CancelAfter(HandshakeTimeout);
        try
        {
            var conn = await ConnectHandshakeAsync(tcp, host, port, password, hsCts.Token);
            if (conn == null) tcp.Close();
            return conn;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Клиент принял TCP, но не отвечает (занят зависшей сессией) — это сбой связи.
            tcp.Close();
            throw new IOException("Handshake timed out");
        }
        catch
        {
            tcp.Close();
            throw;
        }
    }

    private static async Task<SecureConnection?> ConnectHandshakeAsync(
        TcpClient tcp, string host, int port, string password, CancellationToken ct)
    {
        await tcp.ConnectAsync(host, port, ct);

        var stream = tcp.GetStream();

        var handshake = new byte[32];
        await ReadExactAsync(stream, handshake, ct);
        byte[] salt = handshake[..16];
        byte[] challenge = handshake[16..];

        byte[] key = await Task.Run(() => DeriveKey(password, salt), ct);

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

        var conn = new SecureConnection(tcp, key, isClientSide: false);

        // Первое сообщение клиента показывает, какой у него протокол.
        var (type, block) = await conn.ReadBlockAsync(ct);
        try
        {
            conn.Decrypt(type, block);
        }
        catch (CryptographicException)
        {
            conn._legacyPeer = true;
            conn.Decrypt(type, block); // не расшифровалось и так — пароль/данные неверны
        }

        return type == MessageType.AuthOk ? conn : null;
    }

    /// <summary>Authenticated header: direction, message type, sequence number.</summary>
    private byte[]? BuildHeader(bool sending, MessageType type, ulong seq)
    {
        if (_legacyPeer) return null;

        var header = new byte[10];
        header[0] = sending == _isClientSide ? (byte)0 : (byte)1; // 0 = клиент → админ
        header[1] = (byte)type;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(2), seq);
        return header;
    }

    public async Task SendMessageAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(SendTimeout);
        try
        {
            await _writeLock.WaitAsync(sendCts.Token);
            try
            {
                // шифруем под блокировкой: номер сообщения должен совпасть с порядком в канале
                byte[] nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);
                byte[] ciphertext = new byte[payload.Length];
                byte[] tag = new byte[16];

                using (var aes = new AesGcm(_key, 16))
                {
                    aes.Encrypt(nonce, payload, ciphertext, tag, BuildHeader(true, type, _sendSeq));
                }

                int blockLen = 12 + ciphertext.Length + 16;
                var packet = new byte[5 + blockLen];
                packet[0] = (byte)type;
                BitConverter.TryWriteBytes(packet.AsSpan(1), blockLen);
                Buffer.BlockCopy(nonce, 0, packet, 5, 12);
                Buffer.BlockCopy(ciphertext, 0, packet, 17, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, packet, 17 + ciphertext.Length, 16);

                await _stream.WriteAsync(packet, sendCts.Token);
                _sendSeq++;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Send timed out");
        }
    }

    /// <summary>
    /// Receive one message. With <paramref name="idleTimeout"/> the call fails with
    /// IOException when nothing arrives in that time — так обнаруживается соединение,
    /// которое оборвалось молча (без ошибки сокета) и иначе висело бы вечно.
    /// </summary>
    public async Task<(MessageType type, byte[] payload)> ReceiveMessageAsync(
        CancellationToken ct, TimeSpan? idleTimeout = null)
    {
        if (idleTimeout == null)
            return await ReceiveCoreAsync(ct);

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(idleTimeout.Value);
        try
        {
            return await ReceiveCoreAsync(idleCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("No data from remote side");
        }
    }

    private async Task<(MessageType type, byte[] payload)> ReceiveCoreAsync(CancellationToken ct)
    {
        var (type, block) = await ReadBlockAsync(ct);
        return (type, Decrypt(type, block));
    }

    private async Task<(MessageType type, byte[] block)> ReadBlockAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await ReadExactAsync(_stream, header, ct);

        var type = (MessageType)header[0];
        int blockLen = BitConverter.ToInt32(header, 1);

        if (blockLen < 28 || blockLen > 20_000_000)
            throw new InvalidDataException($"Invalid block length: {blockLen}");

        var block = new byte[blockLen];
        await ReadExactAsync(_stream, block, ct);
        return (type, block);
    }

    /// <summary>Decrypts the next expected message; a replayed, reordered or relabelled one fails.</summary>
    private byte[] Decrypt(MessageType type, byte[] block)
    {
        int ciphertextLen = block.Length - 28;
        byte[] plaintext = new byte[ciphertextLen];

        using (var aes = new AesGcm(_key, 16))
        {
            aes.Decrypt(
                block.AsSpan(0, 12),
                block.AsSpan(12, ciphertextLen),
                block.AsSpan(block.Length - 16, 16),
                plaintext,
                BuildHeader(false, type, _recvSeq));
        }

        _recvSeq++;
        return plaintext;
    }

    public bool IsConnected => _tcp.Connected && !_disposed;

    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { }
    }

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
