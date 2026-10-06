using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CxSshClient.Services;

/// <summary>
/// 自实现的 RFB (VNC) 协议客户端。
/// 支持: RFB 3.3 / 3.7 / 3.8, 安全类型 None 与 VNC 认证(DES),
/// 编码: Raw(0) / CopyRect(1) / Hextile(5) / DesktopSize(-223),
/// 像素格式固定请求 32bpp 小端 BGRA, 便于直接渲染。
/// </summary>
public class VncSession : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _writeLock = new();
    private readonly object _fbLock = new();

    public string Host { get; }
    public int Port { get; }
    public string Password { get; set; }
    public bool ViewOnly { get; set; }
    public bool Shared { get; set; } = true;

    private byte[] _framebuffer = Array.Empty<byte>();
    private readonly List<(int X, int Y, int W, int H)> _dirty = new();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string ServerName { get; private set; } = "";
    public bool IsConnected => _tcp?.Connected ?? false;

    public event Action? FramebufferChanged;   // 后台线程触发, UI 侧需自行节流
    public event Action<string>? Status;
    public event Action? Closed;
    public event Action? BellReceived;
    public event Action<string>? ClipboardReceived;

    public VncSession(string host, int port, string password)
    {
        Host = host;
        Port = port;
        Password = password ?? "";
    }

    public async Task ConnectAsync(CancellationToken outer = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = _cts.Token;

        _tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await _tcp.ConnectAsync(Host, Port, connectCts.Token);
        }
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();

        // ---- 版本协商 ----
        var verBuf = new byte[12];
        await ReadExactAsync(verBuf, 12, ct);
        var serverVer = Encoding.ASCII.GetString(verBuf);
        if (!serverVer.StartsWith("RFB "))
            throw new InvalidOperationException($"不是 VNC 服务: {serverVer.Trim()}");
        var major = int.Parse(serverVer.Substring(4, 3));
        var minor = int.Parse(serverVer.Substring(8, 3));
        var atLeast37 = major > 3 || (major == 3 && minor >= 7);
        var atLeast38 = major > 3 || (major == 3 && minor >= 8);
        var clientVer = atLeast38 ? "RFB 003.008\n" : atLeast37 ? "RFB 003.007\n" : "RFB 003.003\n";
        await WriteAsync(Encoding.ASCII.GetBytes(clientVer), ct);

        // ---- 安全类型 ----
        if (atLeast37)
        {
            var nBuf = new byte[1];
            await ReadExactAsync(nBuf, 1, ct);
            var n = nBuf[0];
            if (n == 0)
            {
                await ReadExactAsync(new byte[4], 4, ct); // 失败原因长度
                throw new InvalidOperationException("服务端拒绝了连接 (无可用安全类型)");
            }
            var types = new byte[n];
            await ReadExactAsync(types, n, ct);
            var chosen = types.Contains((byte)2) ? (byte)2 : types.Contains((byte)1) ? (byte)1 : (byte)0;
            if (chosen == 0)
                throw new InvalidOperationException(
                    $"不支持的安全类型: {string.Join(",", types)} (需要 None 或 VNC 认证)");
            if (types.Length > 1 || chosen != 1)
                await WriteAsync(new[] { chosen }, ct);
            if (chosen == 2) await DoVncAuthAsync(ct);
            await ReadSecurityResultAsync(ct);
        }
        else
        {
            var secBuf = new byte[4];
            await ReadExactAsync(secBuf, 4, ct);
            var sec = ReadU32(secBuf, 0);
            if (sec == 0)
            {
                var lenBuf = new byte[4];
                await ReadExactAsync(lenBuf, 4, ct);
                var len = (int)ReadU32(lenBuf, 0);
                var reason = new byte[Math.Clamp(len, 0, 4096)];
                if (reason.Length > 0) await ReadExactAsync(reason, reason.Length, ct);
                throw new InvalidOperationException("服务端拒绝: " + Encoding.ASCII.GetString(reason));
            }
            if (sec == 2)
            {
                await DoVncAuthAsync(ct);
                await ReadSecurityResultAsync(ct);
            }
            // sec == 1: 无认证, 3.3 无 SecurityResult
        }

        // ---- ClientInit ----
        await WriteAsync(new[] { (byte)(Shared ? 1 : 0) }, ct);

        // ---- ServerInit ----
        var si = new byte[24];
        await ReadExactAsync(si, 24, ct);
        Width = (si[0] << 8) | si[1];
        Height = (si[2] << 8) | si[3];
        var nameLen = (int)ReadU32(si, 20);
        if (nameLen > 0)
        {
            var nb = new byte[Math.Clamp(nameLen, 0, 8192)];
            await ReadExactAsync(nb, nb.Length, ct);
            ServerName = Encoding.UTF8.GetString(nb);
        }

        lock (_fbLock)
        {
            _framebuffer = new byte[Width * Height * 4];
        }

        // ---- SetPixelFormat: 32bpp, depth 24, little-endian, true colour, BGRA ----
        var spf = new byte[20];
        spf[0] = 0;                    // message-type
        spf[1] = 3;                    // padding
        spf[4] = 32;                   // bits-per-pixel
        spf[5] = 24;                   // depth
        spf[6] = 0;                    // big-endian-flag = 0
        spf[7] = 1;                    // true-colour-flag
        WriteU16(spf, 8, 255);   // red-max
        WriteU16(spf, 10, 255);  // green-max
        WriteU16(spf, 12, 255);  // blue-max
        spf[14] = 16;                  // red-shift
        spf[15] = 8;                   // green-shift
        spf[16] = 0;                   // blue-shift
        await WriteAsync(spf, ct);

        // ---- SetEncodings ----
        int[] encodings = { 5, 1, 0, -223 };   // Hextile, CopyRect, Raw, DesktopSize
        var se = new byte[4 + encodings.Length * 4];
        se[0] = 2;
        WriteU16(se, 2, (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++)
            WriteI32(se, 4 + i * 4, encodings[i]);
        await WriteAsync(se, ct);

        Status?.Invoke($"已连接 {Host}:{Port}  {Width}×{Height}  {ServerName}".Trim());
        _loop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task DoVncAuthAsync(CancellationToken ct)
    {
        var challenge = new byte[16];
        await ReadExactAsync(challenge, 16, ct);
        var response = EncryptVncPassword(Password, challenge);
        await WriteAsync(response, ct);
    }

    private async Task ReadSecurityResultAsync(CancellationToken ct)
    {
        var buf = new byte[4];
        await ReadExactAsync(buf, 4, ct);
        var result = ReadU32(buf, 0);
        if (result != 0)
            throw new InvalidOperationException("VNC 认证失败 (密码错误?)");
    }

    private static byte[] EncryptVncPassword(string password, byte[] challenge)
    {
        var key = new byte[8];
        var bytes = Encoding.ASCII.GetBytes(password ?? "");
        for (var i = 0; i < 8; i++)
            key[i] = i < bytes.Length ? ReverseBits(bytes[i]) : (byte)0;
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;
        using var enc = des.CreateEncryptor();
        return enc.TransformFinalBlock(challenge, 0, challenge.Length);
    }

    private static byte ReverseBits(byte b)
    {
        byte r = 0;
        for (var i = 0; i < 8; i++)
            if ((b & (1 << i)) != 0) r |= (byte)(1 << (7 - i));
        return r;
    }

    // ================= 读循环 =================

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await RequestUpdateAsync(false, ct);
            while (!ct.IsCancellationRequested)
            {
                var typeBuf = new byte[1];
                await ReadExactAsync(typeBuf, 1, ct);
                switch (typeBuf[0])
                {
                    case 0: await HandleFramebufferUpdateAsync(ct); break;
                    case 1: await HandleColourMapAsync(ct); break;
                    case 2: BellReceived?.Invoke(); break;
                    case 3: await HandleServerCutTextAsync(ct); break;
                    default:
                        throw new IOException($"未知的服务器消息类型 {typeBuf[0]}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Status?.Invoke("连接中断: " + ex.Message);
        }
        finally
        {
            Closed?.Invoke();
        }
    }

    private async Task HandleColourMapAsync(CancellationToken ct)
    {
        var head = new byte[5];
        await ReadExactAsync(head, 5, ct);
        var count = (head[3] << 8) | head[4];
        var body = new byte[count * 6];
        await ReadExactAsync(body, body.Length, ct);
    }

    private async Task HandleServerCutTextAsync(CancellationToken ct)
    {
        var head = new byte[7];
        await ReadExactAsync(head, 7, ct);
        var len = (int)ReadU32(head, 3);
        if (len <= 0) return;
        var buf = new byte[Math.Clamp(len, 0, 1024 * 1024)];
        await ReadExactAsync(buf, buf.Length, ct);
        try { ClipboardReceived?.Invoke(Encoding.GetEncoding("ISO-8859-1").GetString(buf)); }
        catch { }
    }

    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        var head = new byte[3];
        await ReadExactAsync(head, 3, ct);
        var nRects = (head[1] << 8) | head[2];
        var changed = false;

        for (var i = 0; i < nRects; i++)
        {
            var rh = new byte[12];
            await ReadExactAsync(rh, 12, ct);
            var x = (rh[0] << 8) | rh[1];
            var y = (rh[2] << 8) | rh[3];
            var w = (rh[4] << 8) | rh[5];
            var h = (rh[6] << 8) | rh[7];
            var enc = ReadI32(rh, 8);

            switch (enc)
            {
                case 0: await DecodeRawAsync(x, y, w, h, ct); changed = true; break;
                case 1: await DecodeCopyRectAsync(x, y, w, h, ct); changed = true; break;
                case 5: await DecodeHextileAsync(x, y, w, h, ct); changed = true; break;
                case -223: // DesktopSize 伪编码
                    Resize(w, h);
                    changed = true;
                    break;
                default:
                    throw new IOException($"服务端返回了未请求的编码 {enc}");
            }
        }

        if (changed) FramebufferChanged?.Invoke();
        await RequestUpdateAsync(true, ct);
    }

    private void Resize(int w, int h)
    {
        lock (_fbLock)
        {
            Width = w;
            Height = h;
            _framebuffer = new byte[w * h * 4];
            _dirty.Add((0, 0, w, h));
        }
    }

    private async Task DecodeRawAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        var pixels = new byte[w * h * 4];
        await ReadExactAsync(pixels, pixels.Length, ct);
        lock (_fbLock)
        {
            if (x + w > Width || y + h > Height) return;
            for (var row = 0; row < h; row++)
            {
                Buffer.BlockCopy(pixels, row * w * 4,
                    _framebuffer, ((y + row) * Width + x) * 4, w * 4);
            }
            _dirty.Add((x, y, w, h));
        }
    }

    private async Task DecodeCopyRectAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        var src = new byte[4];
        await ReadExactAsync(src, 4, ct);
        var sx = (src[0] << 8) | src[1];
        var sy = (src[2] << 8) | src[3];
        lock (_fbLock)
        {
            if (x + w > Width || y + h > Height || sx + w > Width || sy + h > Height) return;
            // 逐行拷贝(处理重叠)
            var step = sy < y ? -1 : 1;
            var startRow = step < 0 ? h - 1 : 0;
            var tmp = new byte[w * 4];
            for (var i = 0; i < h; i++)
            {
                var row = startRow + step * i;
                Buffer.BlockCopy(_framebuffer, ((sy + row) * Width + sx) * 4, tmp, 0, w * 4);
                Buffer.BlockCopy(tmp, 0, _framebuffer, ((y + row) * Width + x) * 4, w * 4);
            }
            _dirty.Add((x, y, w, h));
        }
    }

    private async Task DecodeHextileAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        lock (_fbLock)
        {
            if (x + w > Width || y + h > Height) return;
        }
        var bg = new byte[4];
        var fg = new byte[4];
        var sub = new byte[1];
        var colorBuf = new byte[4];
        var xyBuf = new byte[2];
        var tileBuf = new byte[16 * 16 * 4];
        await DecodeHextileCoreAsync(x, y, w, h, ct, bg, fg, sub, colorBuf, xyBuf, tileBuf);
    }

    private async Task DecodeHextileCoreAsync(int x, int y, int w, int h, CancellationToken ct,
        byte[] bg, byte[] fg, byte[] sub, byte[] colorBuf, byte[] xyBuf, byte[] tileBuf)
    {
        const int tile = 16;
        for (var ty = y; ty < y + h; ty += tile)
        {
            var th = Math.Min(tile, y + h - ty);
            for (var tx = x; tx < x + w; tx += tile)
            {
                var tw = Math.Min(tile, x + w - tx);
                await ReadExactAsync(sub, 1, ct);
                var s = sub[0];

                if ((s & 1) != 0) // Raw
                {
                    var n = tw * th * 4;
                    await ReadExactAsync(tileBuf, n, ct);
                    lock (_fbLock)
                    {
                        for (var row = 0; row < th; row++)
                            Buffer.BlockCopy(tileBuf, row * tw * 4, _framebuffer,
                                ((ty + row) * Width + tx) * 4, tw * 4);
                    }
                    continue;
                }

                if ((s & 2) != 0) { await ReadExactAsync(bg, 4, ct); }
                if ((s & 4) != 0) { await ReadExactAsync(fg, 4, ct); }

                lock (_fbLock)
                {
                    for (var row = 0; row < th; row++)
                        for (var col = 0; col < tw; col++)
                            Buffer.BlockCopy(bg, 0, _framebuffer, ((ty + row) * Width + tx + col) * 4, 4);

                    if ((s & 8) != 0)
                    {
                        // nSubrects 需要读取, 但此时在锁内; 先读出再处理
                    }
                }

                if ((s & 8) != 0)
                {
                    await ReadExactAsync(xyBuf, 1, ct);
                    var nSub = xyBuf[0];
                    for (var i = 0; i < nSub; i++)
                    {
                        var col = fg;
                        if ((s & 16) != 0)
                        {
                            await ReadExactAsync(colorBuf, 4, ct);
                            col = colorBuf;
                        }
                        await ReadExactAsync(xyBuf, 2, ct);
                        var sx = xyBuf[0] >> 4;
                        var sy = xyBuf[0] & 0x0F;
                        var sw = (xyBuf[1] >> 4) + 1;
                        var sh = (xyBuf[1] & 0x0F) + 1;
                        lock (_fbLock)
                        {
                            for (var row = sy; row < sy + sh && row < th; row++)
                                for (var c = sx; c < sx + sw && c < tw; c++)
                                    Buffer.BlockCopy(col, 0, _framebuffer,
                                        ((ty + row) * Width + tx + c) * 4, 4);
                        }
                    }
                }
            }
        }

        lock (_fbLock) _dirty.Add((x, y, w, h));
    }

    // ================= 发送 =================

    private async Task RequestUpdateAsync(bool incremental, CancellationToken ct)
    {
        var msg = new byte[10];
        msg[0] = 3;
        msg[1] = (byte)(incremental ? 1 : 0);
        WriteU16(msg, 2, 0);
        WriteU16(msg, 4, 0);
        WriteU16(msg, 6, (ushort)Math.Min(Width, 65535));
        WriteU16(msg, 8, (ushort)Math.Min(Height, 65535));
        await WriteAsync(msg, ct);
    }

    /// <summary>请求整屏刷新</summary>
    public void RequestFullUpdate()
    {
        if (_stream is null || _cts is null || _cts.IsCancellationRequested) return;
        _ = RequestUpdateAsync(false, _cts.Token).ContinueWith(_ => { });
    }

    public void SendPointer(int x, int y, int buttons)
    {
        if (ViewOnly || _stream is null || _cts is null || _cts.IsCancellationRequested) return;
        var msg = new byte[6];
        msg[0] = 5;
        msg[1] = (byte)buttons;
        WriteU16(msg, 2, (ushort)Math.Clamp(x, 0, 65535));
        WriteU16(msg, 4, (ushort)Math.Clamp(y, 0, 65535));
        TryWrite(msg);
    }

    public void SendKey(int keysym, bool pressed)
    {
        if (ViewOnly || _stream is null || _cts is null || _cts.IsCancellationRequested) return;
        var msg = new byte[8];
        msg[0] = 4;
        msg[1] = (byte)(pressed ? 1 : 0);
        WriteU32(msg, 4, (uint)keysym);
        TryWrite(msg);
    }

    public void SendClipboard(string text)
    {
        if (_stream is null || _cts is null || _cts.IsCancellationRequested) return;
        byte[] payload;
        try { payload = Encoding.GetEncoding("ISO-8859-1").GetBytes(text); }
        catch { payload = Encoding.ASCII.GetBytes(text); }
        var msg = new byte[8 + payload.Length];
        msg[0] = 6;
        WriteU32(msg, 4, (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, msg, 8, payload.Length);
        TryWrite(msg);
    }

    private void TryWrite(byte[] data)
    {
        try
        {
            lock (_writeLock)
            {
                _stream?.Write(data, 0, data.Length);
            }
        }
        catch { }
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (_stream is null) throw new IOException("未连接");
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        if (_stream is null) throw new IOException("未连接");
        var offset = 0;
        while (offset < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n <= 0) throw new IOException("服务端已关闭连接");
            offset += n;
        }
    }

    // ================= 小工具 =================

    private static ushort ReadU16(byte[] b, int i) => (ushort)((b[i] << 8) | b[i + 1]);
    private static uint ReadU32(byte[] b, int i) => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
    private static int ReadI32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static void WriteU16(byte[] b, int i, int v) { b[i] = (byte)(v >> 8); b[i + 1] = (byte)v; }
    private static void WriteU32(byte[] b, int i, uint v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
    private static void WriteI32(byte[] b, int i, int v) => WriteU32(b, i, (uint)v);

    /// <summary>取出并清空脏矩形(供渲染线程使用)</summary>
    public List<(int X, int Y, int W, int H)> TakeDirty()
    {
        lock (_fbLock)
        {
            var copy = new List<(int, int, int, int)>(_dirty);
            _dirty.Clear();
            return copy;
        }
    }

    public (byte[] Buffer, int Width, int Height) GetFramebuffer()
    {
        lock (_fbLock) return (_framebuffer, Width, Height);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
    }
}

/// <summary>Windows 虚拟键 → X11 keysym 映射</summary>
public static class VncKeys
{
    public static int FromVirtualKey(Windows.System.VirtualKey key)
    {
        var k = (int)key;
        // 字母: 'a'-'z' (0x61 起), 大写由 Shift 键状态决定
        if (k >= 0x41 && k <= 0x5A) return 0x61 + (k - 0x41);
        // 数字: '0'-'9'
        if (k >= 0x30 && k <= 0x39) return k;
        // 小键盘 0-9
        if (k >= 0x60 && k <= 0x69) return 0xFFB0 + (k - 0x60);
        return k switch
        {
            0x08 => 0xFF08, // Backspace
            0x09 => 0xFF09, // Tab
            0x0D => 0xFF0D, // Enter
            0x13 => 0xFF13, // Pause
            0x14 => 0xFFE5, // CapsLock
            0x1B => 0xFF1B, // Escape
            0x20 => 0x0020, // Space
            0x21 => 0xFF55, // PageUp
            0x22 => 0xFF56, // PageDown
            0x23 => 0xFF57, // End
            0x24 => 0xFF50, // Home
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            0x2C => 0xFF61, // PrintScreen
            0x2D => 0xFF63, // Insert
            0x2E => 0xFFFF, // Delete
            0x5B => 0xFFEB, // LeftWindows
            0x5C => 0xFFEC, // RightWindows
            0x5D => 0xFF67, // Menu / Apps
            0x6A => 0xFFAA, // Multiply
            0x6B => 0xFFAB, // Add
            0x6D => 0xFFAD, // Subtract
            0x6E => 0xFFAE, // Decimal
            0x6F => 0xFFAF, // Divide
            0x70 => 0xFFBE, 0x71 => 0xFFBF, 0x72 => 0xFFC0, 0x73 => 0xFFC1,
            0x74 => 0xFFC2, 0x75 => 0xFFC3, 0x76 => 0xFFC4, 0x77 => 0xFFC5,
            0x78 => 0xFFC6, 0x79 => 0xFFC7, 0x7A => 0xFFC8, 0x7B => 0xFFC9,
            0x90 => 0xFF7F, // NumLock
            0x91 => 0xFF14, // ScrollLock
            0xA0 => 0xFFE1, // LeftShift
            0xA1 => 0xFFE2, // RightShift
            0xA2 => 0xFFE3, // LeftCtrl
            0xA3 => 0xFFE4, // RightCtrl
            0xA4 => 0xFFE9, // LeftAlt
            0xA5 => 0xFFEA, // RightAlt
            0xBA => 0x003B, // ; :
            0xBB => 0x003D, // = +
            0xBC => 0x002C, // , <
            0xBD => 0x002D, // - _
            0xBE => 0x002E, // . >
            0xBF => 0x002F, // / ?
            0xC0 => 0x0060, // ` ~
            0xDB => 0x005B, // [ {
            0xDC => 0x005C, // \ |
            0xDD => 0x005D, // ] }
            0xDE => 0x0027, // ' "
            _ => 0
        };
    }
}
