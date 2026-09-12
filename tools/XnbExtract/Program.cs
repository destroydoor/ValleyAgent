// XnbExtract — 解包 Stardew Valley 1.6.15+ (ConcernedApe 定制 MonoGame 引擎) 的 XNB 纹理为 PNG。
// 实现依据：反编译游戏自带 MonoGame.Framework.dll 的
//   ContentManager.GetContentReaderFromXnb (XNB 头/flags: 0x80=LZX, 0x40=LZ4, 大小 LE)
//   ContentTypeReaderManager.LoadAssetReaders (类型清单 7-bit)
//   Texture2DReader (载荷: format + 打包宽高 uint32 + levelCount + 每级 [dataSize + data])
//   MonoGame.Framework.Utilities.LzxDecoderStream / Lz4DecoderStream (原样移植解码器)
using System.IO.Compression;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: XnbExtract <input.xnb|inputDir> <outputDir>");
    return 1;
}

string input = Path.GetFullPath(args[0]);
string outputArg = Path.GetFullPath(args[1]);
bool isDir = Directory.Exists(input);

// 参数 2 用法：
//   - 输出目录：Directory.Exists(outputArg) = true（批量模式）→ 多个 xnb → outputArg/<rel>.png
//   - 单文件输出：用户传的是目标 .png 路径（不存在或扩展名是 .png）→ 把它的目录当 outputDir，文件名当 outName
//     目录模式、单文件模式 都支持 outputArg = "<dir>" 或 "<dir>\<file>.png"
string outputDir;
string? singleOutName;  // 非空 = 单文件模式；为空 = 批量模式
if (Directory.Exists(outputArg))
{
    outputDir = outputArg;
    singleOutName = null;
}
else if (string.Equals(Path.GetExtension(outputArg), ".png", StringComparison.OrdinalIgnoreCase))
{
    // 单文件模式：取目录为 outputDir，文件名固定
    outputDir = Path.GetDirectoryName(outputArg)!;
    singleOutName = Path.GetFileName(outputArg);
    if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
}
else
{
    // 既不是已存在目录，也不是 .png 路径 → 当目录用（自动创建）
    outputDir = outputArg;
    singleOutName = null;
    Directory.CreateDirectory(outputDir);
}

if (isDir)
{
    string[] files = Directory.GetFiles(input, "*.xnb", SearchOption.AllDirectories);
    int ok = 0, fail = 0;
    foreach (string f in files)
    {
        string rel = Path.GetRelativePath(input, f);
        string outFile = Path.Combine(outputDir, Path.ChangeExtension(rel, ".png"));
        try
        {
            if (TryExtractTexture(f, outFile, out string note))
            {
                Console.WriteLine($"OK    {rel} -> {note}");
                ok++;
            }
            else
            {
                Console.WriteLine($"SKIP  {rel}: {note}");
                fail++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  {rel}: {ex.Message}");
            fail++;
        }
    }
    Console.WriteLine($"Done: {ok} extracted, {fail} skipped/failed.");
    return fail == 0 ? 0 : 2;
}
else
{
    // 单文件模式：outFile = singleOutName（如果用户传了 .png 路径就用用户定的完整路径）否则 = input 改后缀放在 outputDir
    string outFile = singleOutName != null
        ? Path.Combine(outputDir, singleOutName)
        : Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(input), ".png"));
    if (TryExtractTexture(input, outFile, out string note))
    {
        Console.WriteLine($"OK    {input} -> {note}  ->  {outFile}");
        return 0;
    }
    Console.WriteLine($"SKIP  {note}");
    return 2;
}

static bool TryExtractTexture(string xnbPath, string outPngPath, out string note)
{
    note = "";
    byte[] file = File.ReadAllBytes(xnbPath);
    if (file.Length < 10 || file[0] != (byte)'X' || file[1] != (byte)'N' || file[2] != (byte)'B')
    {
        note = "not an XNB file";
        return false;
    }

    byte version = file[4];
    if (version != 5 && version != 4)
    {
        note = $"unsupported XNB version {version}";
        return false;
    }
    byte flags = file[5];
    bool lzx = (flags & 0x80) != 0;
    bool lz4 = (flags & 0x40) != 0;
    int fileSize = BitConverter.ToInt32(file, 6); // LE，含全部字节

    byte[] payload;
    using (var raw = new MemoryStream(file, 10, file.Length - 10, writable: false))
    {
        if (lzx)
        {
            int decompressedSize = ReadInt32_FromStream(raw);
            int compressedSize = fileSize - 14;
            using var dec = new LzxDecoderStream(raw, decompressedSize, compressedSize);
            payload = ReadFully(dec);
        }
        else if (lz4)
        {
            ReadInt32_FromStream(raw); // LZ4 路径忽略 decompressedSize（与游戏实现一致）
            using var dec = new Lz4DecoderStream(raw);
            payload = ReadFully(dec);
        }
        else
        {
            payload = ReadFully(raw);
        }
    }

    // 类型清单：typeCount(7-bit) + 每个 [typeName(string) + version(int32)]，随后 readerIndex(7-bit)、sharedResourceCount(7-bit)
    int pos = 0;
    int typeCount = Read7BitInt(payload, ref pos);
    var typeNames = new string[typeCount];
    var typeVersions = new int[typeCount];
    for (int i = 0; i < typeCount; i++)
    {
        typeNames[i] = ReadString(payload, ref pos);
        typeVersions[i] = ReadInt32_FromBuffer(payload, ref pos);
    }
    int readerIndex = Read7BitInt(payload, ref pos);
    // 共享资源数（MonoGame ContentManager 流程：类型清单 → primary reader index → shared resource count → 主资产数据）
    int sharedResourceCount = Read7BitInt(payload, ref pos);
    // readerIndex 可能 1-based（标准 XNA）或 0-based（某些 MonoGame 构建），两者都尝试
    string readerType = "?";
    if (readerIndex >= 1 && readerIndex <= typeNames.Length)
        readerType = typeNames[readerIndex - 1];
    else if (readerIndex >= 0 && readerIndex < typeNames.Length)
        readerType = typeNames[readerIndex];
    if (!readerType.Contains("Texture2DReader", StringComparison.Ordinal))
    {
        string hex = BitConverter.ToString(payload.AsSpan(0, Math.Min(64, payload.Length)).ToArray()).Replace("-","");
        string names = typeNames.Length == 0 ? "(empty)" : string.Join(" | ", typeNames.Select((n,i)=>$"[{i}]={n}"));
        note = $"not a texture (reader={readerType} idx={readerIndex}/{typeCount} shared={sharedResourceCount} pos={pos} payloadLen={payload.Length} head={hex} names={names})";
        return false;
    }

    // Texture2DReader 载荷（Stardew Valley 定制 MonoGame：format + width + height + levelCount + levels[]，非 packed）
    int surfaceFormat = ReadInt32_FromBuffer(payload, ref pos);
    uint widthU = ReadUInt32_FromBuffer(payload, ref pos);
    uint heightU = ReadUInt32_FromBuffer(payload, ref pos);
    int levelCount = ReadInt32_FromBuffer(payload, ref pos);
    int width = (int)widthU;
    int height = (int)heightU;
    uint imageW = widthU;
    uint imageH = heightU;

    int dataSize = ReadInt32_FromBuffer(payload, ref pos);
    byte[] data = payload.AsSpan(pos, Math.Min(dataSize, payload.Length - pos)).ToArray();

    if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
    {
        string? d = Path.GetDirectoryName(outPngPath);
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        File.WriteAllBytes(outPngPath, data);
        note = $"PNG {width}x{height} (img {imageW}x{imageH}) fmt={surfaceFormat} levels={levelCount}";
        return true;
    }

    // 原始像素：Color(0) / Bgra32(21) / Rgba32(32) 都为 4 字节
    // MonoGame 的 Color 是 struct,字段顺序 R,G,B,A,序列化按字段顺序写入 XNB(不是 packed uint 的小端 B,G,R,A)
    // Texture2D 上传 GPU 时 GPU 自己处理字节序,XNB 文件里就是 [R,G,B,A]
    int bpp = surfaceFormat switch { 0 or 21 or 32 => 4, 1 => 2, 3 => 2, 12 => 1, _ => 0 };
    if (bpp > 0 && levelCount > 0 && width > 0 && height > 0 && data.Length >= width * height * bpp)
    {
        byte[] px = data[..(width * height * bpp)];
        if (bpp == 4)
        {
            // 直接拷贝,源已经是 [R,G,B,A] 顺序
            byte[] rgba = new byte[px.Length];
            Buffer.BlockCopy(px, 0, rgba, 0, px.Length);
            px = rgba;
        }
        else if (bpp == 2)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0, o = 0; i < px.Length; i += 2, o += 4)
            {
                ushort v = (ushort)(px[i] | (px[i + 1] << 8));
                if (surfaceFormat == 1) // Bgr565
                {
                    rgba[o] = (byte)((v & 0x1F) * 255 / 31);
                    rgba[o + 1] = (byte)(((v >> 5) & 0x3F) * 255 / 63);
                    rgba[o + 2] = (byte)(((v >> 11) & 0x1F) * 255 / 31);
                    rgba[o + 3] = 255;
                }
                else // Bgra4444
                {
                    rgba[o] = (byte)(((v >> 8) & 0xF) * 255 / 15);
                    rgba[o + 1] = (byte)(((v >> 4) & 0xF) * 255 / 15);
                    rgba[o + 2] = (byte)((v & 0xF) * 255 / 15);
                    rgba[o + 3] = (byte)(((v >> 12) & 0xF) * 255 / 15);
                }
            }
            px = rgba;
        }
        string? dir = Path.GetDirectoryName(outPngPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outPngPath, EncodePng(width, height, px));
        note = $"RAW {width}x{height} (img {imageW}x{imageH}) fmt={surfaceFormat} levels={levelCount}";
        return true;
    }

    note = $"unhandled texture data (w={width} h={height} fmt={surfaceFormat} levels={levelCount} dataLen={data.Length} img={imageW}x{imageH} shared={sharedResourceCount} pos={pos})";
    return false;
}

static byte[] ReadFully(Stream s)
{
    using var outMs = new MemoryStream();
    s.CopyTo(outMs);
    return outMs.ToArray();
}

static int ReadInt32_FromStream(Stream s)
{
    byte[] b = new byte[4];
    s.ReadExactly(b);
    return BitConverter.ToInt32(b, 0);
}

static int Read7BitInt(byte[] buf, ref int pos)
{
    int result = 0, shift = 0;
    while (true)
    {
        if (pos >= buf.Length) throw new InvalidDataException("unexpected end of payload");
        byte b = buf[pos++];
        result |= (b & 0x7F) << shift;
        if ((b & 0x80) == 0) break;
        shift += 7;
        if (shift > 35) throw new InvalidDataException("7-bit int too long");
    }
    return result;
}

static string ReadString(byte[] buf, ref int pos)
{
    int len = Read7BitInt(buf, ref pos);
    if (pos + len > buf.Length) throw new InvalidDataException("string exceeds payload");
    string s = System.Text.Encoding.UTF8.GetString(buf, pos, len);
    pos += len;
    return s;
}

static int ReadInt32_FromBuffer(byte[] buf, ref int pos)
{
    if (pos + 4 > buf.Length) throw new InvalidDataException("int32 exceeds payload");
    int v = BitConverter.ToInt32(buf, pos);
    pos += 4;
    return v;
}

static uint ReadUInt32_FromBuffer(byte[] buf, ref int pos)
{
    if (pos + 4 > buf.Length) throw new InvalidDataException("uint32 exceeds payload");
    uint v = BitConverter.ToUInt32(buf, pos);
    pos += 4;
    return v;
}

// 最小 PNG 编码器（8-bit RGBA、逐行 filter 0、zlib deflate）。
// IDAT 数据须为 zlib 格式：2 字节 zlib header（0x78 0x9C = 最大压缩/deflate）+ deflate 流 + 4 字节 adler32。
// .NET DeflateStream 只产 raw deflate 流，所以手动补 zlib header 和 adler32。
static byte[] EncodePng(int width, int height, byte[] rgba)
{
    using var png = new MemoryStream();
    png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    WriteChunk(png, "IHDR", BuildIhdr(width, height));
    byte[] raw = new byte[height * (1 + width * 4)];
    int di = 0;
    for (int y = 0; y < height; y++)
    {
        raw[di++] = 0; // filter: none
        Array.Copy(rgba, y * width * 4, raw, di, width * 4);
        di += width * 4;
    }
    // 1) 算 adler32（对未压缩原始数据）
    uint adler = Adler32(raw);
    // 2) 压缩为 raw deflate
    using var deflateMs = new MemoryStream();
    using (var ds = new DeflateStream(deflateMs, CompressionLevel.Optimal, leaveOpen: true))
    {
        ds.Write(raw, 0, raw.Length);
    }
    byte[] deflateBytes = deflateMs.ToArray();
    // 3) 拼 zlib 流：header + deflate + adler32(BE)
    using var zlibMs = new MemoryStream();
    zlibMs.Write(new byte[] { 0x78, 0x9C });  // zlib header: CMF=8 (deflate), FLG=0x9C (最大压缩)
    zlibMs.Write(deflateBytes, 0, deflateBytes.Length);
    zlibMs.WriteByte((byte)((adler >> 24) & 0xFF));
    zlibMs.WriteByte((byte)((adler >> 16) & 0xFF));
    zlibMs.WriteByte((byte)((adler >> 8) & 0xFF));
    zlibMs.WriteByte((byte)(adler & 0xFF));
    WriteChunk(png, "IDAT", zlibMs.ToArray());
    WriteChunk(png, "IEND", Array.Empty<byte>());
    return png.ToArray();
}

static uint Adler32(byte[] data)
{
    const uint MOD = 65521;
    uint a = 1, b = 0;
    for (int i = 0; i < data.Length; i++)
    {
        a = (a + data[i]) % MOD;
        b = (b + a) % MOD;
    }
    return (b << 16) | a;
}

static byte[] BuildIhdr(int width, int height)
{
    byte[] b = new byte[13];
    WriteBigEndianInt32Bytes(b, 0, width);
    WriteBigEndianInt32Bytes(b, 4, height);
    b[8] = 8;  // bit depth
    b[9] = 6;  // color type: RGBA
    return b;
}

static void WriteChunk(Stream s, string type, byte[] data)
{
    byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    WriteBigEndianInt32Stream(s, data.Length);
    s.Write(typeBytes);
    s.Write(data);
    uint crc = Crc32(typeBytes.Concat(data).ToArray());
    WriteBigEndianInt32Stream(s, unchecked((int)crc));
}

static void WriteBigEndianInt32Stream(Stream s, int value)
{
    byte[] b = new byte[4];
    WriteBigEndianInt32Bytes(b, 0, value);
    s.Write(b);
}

static void WriteBigEndianInt32Bytes(byte[] buf, int offset, int value)
{
    buf[offset] = (byte)(value >> 24);
    buf[offset + 1] = (byte)(value >> 16);
    buf[offset + 2] = (byte)(value >> 8);
    buf[offset + 3] = (byte)value;
}

static uint Crc32(byte[] data)
{
    uint crc = 0xFFFFFFFF;
    foreach (byte b in data)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
        {
            crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
    }
    return ~crc;
}

// ===== 以下为 MonoGame.Framework.dll 解码器原样移植（保持游戏运行时行为一致） =====

internal sealed class LzxDecoderStream : Stream
{
    private readonly LzxDecoder dec;
    private MemoryStream decompressedStream = null!; // 由 Decompress 在构造函数内初始化

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public LzxDecoderStream(Stream input, int decompressedSize, int compressedSize)
    {
        dec = new LzxDecoder(16);
        Decompress(input, decompressedSize, compressedSize);
    }

    private void Decompress(Stream stream, int decompressedSize, int compressedSize)
    {
        decompressedStream = new MemoryStream(decompressedSize);
        long position = stream.Position;
        long num = position;
        while (num - position < compressedSize)
        {
            int num2 = stream.ReadByte();
            int num3 = stream.ReadByte();
            int num4 = (num2 << 8) | num3;
            int num5 = 32768;
            if (num2 == 255)
            {
                int num6 = num3;
                num3 = (byte)stream.ReadByte();
                num5 = (num6 << 8) | num3;
                byte num7 = (byte)stream.ReadByte();
                num3 = (byte)stream.ReadByte();
                num4 = (num7 << 8) | num3;
                num += 5;
            }
            else
            {
                num += 2;
            }
            if (num4 == 0 || num5 == 0)
            {
                break;
            }
            dec.Decompress(stream, num4, decompressedStream, num5);
            num += num4;
            stream.Seek(num, SeekOrigin.Begin);
        }
        if (decompressedStream.Position != decompressedSize)
        {
            throw new InvalidDataException("LZX decompression failed.");
        }
        decompressedStream.Seek(0L, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => decompressedStream.Read(buffer, offset, count);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
    public override void SetLength(long value) => throw new NotImplementedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
}

internal sealed class LzxDecoder
{
    private sealed class BitBuffer
    {
        private uint buffer;
        private byte bitsleft;
        private readonly Stream byteStream;

        public BitBuffer(Stream stream)
        {
            byteStream = stream;
            InitBitStream();
        }

        public void InitBitStream()
        {
            buffer = 0u;
            bitsleft = 0;
        }

        public void EnsureBits(byte bits)
        {
            while (bitsleft < bits)
            {
                int num = (byte)byteStream.ReadByte();
                int num2 = (byte)byteStream.ReadByte();
                buffer |= (uint)(((num2 << 8) | num) << 16 - bitsleft);
                bitsleft += 16;
            }
        }

        public uint PeekBits(byte bits) => buffer >> 32 - bits;
        public void RemoveBits(byte bits) { buffer <<= bits; bitsleft -= bits; }

        public uint ReadBits(byte bits)
        {
            uint result = 0u;
            if (bits > 0)
            {
                EnsureBits(bits);
                result = PeekBits(bits);
                RemoveBits(bits);
            }
            return result;
        }

        public uint GetBuffer() => buffer;
        public byte GetBitsLeft() => bitsleft;
    }

    private struct LzxState
    {
        public uint R0;
        public uint R1;
        public uint R2;
        public ushort main_elements;
        public int header_read;
        public LzxConstants.BLOCKTYPE block_type;
        public uint block_length;
        public uint block_remaining;
        public uint frames_read;
        public int intel_filesize;
        public int intel_curpos;
        public int intel_started;
        public ushort[] PRETREE_table;
        public byte[] PRETREE_len;
        public ushort[] MAINTREE_table;
        public byte[] MAINTREE_len;
        public ushort[] LENGTH_table;
        public byte[] LENGTH_len;
        public ushort[] ALIGNED_table;
        public byte[] ALIGNED_len;
        public uint actual_size;
        public byte[] window;
        public uint window_size;
        public uint window_posn;
    }

    public static uint[] position_base = null!; // 构造函数内首次运行时初始化
    public static byte[] extra_bits = null!; // 构造函数内首次运行时初始化

    private LzxState m_state;

    public LzxDecoder(int window)
    {
        uint num = (uint)(1 << window);
        if (window < 15 || window > 21)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
        m_state = default;
        m_state.actual_size = 0u;
        m_state.window = new byte[num];
        for (int i = 0; i < num; i++)
        {
            m_state.window[i] = 220;
        }
        m_state.actual_size = num;
        m_state.window_size = num;
        m_state.window_posn = 0u;
        if (extra_bits == null)
        {
            extra_bits = new byte[52];
            int j = 0;
            int num2 = 0;
            for (; j <= 50; j += 2)
            {
                extra_bits[j] = (extra_bits[j + 1] = (byte)num2);
                if (j != 0 && num2 < 17)
                {
                    num2++;
                }
            }
        }
        if (position_base == null)
        {
            position_base = new uint[51];
            int k = 0;
            int num3 = 0;
            for (; k <= 50; k++)
            {
                position_base[k] = (uint)num3;
                num3 += 1 << extra_bits[k];
            }
        }
        int num4 = window switch
        {
            20 => 42,
            21 => 50,
            _ => window << 1,
        };
        m_state.R0 = (m_state.R1 = (m_state.R2 = 1u));
        m_state.main_elements = (ushort)(256 + (num4 << 3));
        m_state.header_read = 0;
        m_state.frames_read = 0u;
        m_state.block_remaining = 0u;
        m_state.block_type = LzxConstants.BLOCKTYPE.INVALID;
        m_state.intel_curpos = 0;
        m_state.intel_started = 0;
        m_state.PRETREE_table = new ushort[104];
        m_state.PRETREE_len = new byte[84];
        m_state.MAINTREE_table = new ushort[5408];
        m_state.MAINTREE_len = new byte[720];
        m_state.LENGTH_table = new ushort[4596];
        m_state.LENGTH_len = new byte[314];
        m_state.ALIGNED_table = new ushort[144];
        m_state.ALIGNED_len = new byte[72];
        for (int l = 0; l < 656; l++)
        {
            m_state.MAINTREE_len[l] = 0;
        }
        for (int m = 0; m < 250; m++)
        {
            m_state.LENGTH_len[m] = 0;
        }
    }

    public int Decompress(Stream inData, int inLen, Stream outData, int outLen)
    {
        BitBuffer bitBuffer = new BitBuffer(inData);
        long position = inData.Position;
        long num = inData.Position + inLen;
        byte[] window = m_state.window;
        uint num2 = m_state.window_posn;
        uint window_size = m_state.window_size;
        uint num3 = m_state.R0;
        uint num4 = m_state.R1;
        uint num5 = m_state.R2;
        int num6 = outLen;
        bitBuffer.InitBitStream();
        if (m_state.header_read == 0)
        {
            if (bitBuffer.ReadBits(1) != 0)
            {
                uint num7 = bitBuffer.ReadBits(16);
                uint num8 = bitBuffer.ReadBits(16);
                m_state.intel_filesize = (int)((num7 << 16) | num8);
            }
            m_state.header_read = 1;
        }
        while (num6 > 0)
        {
            if (m_state.block_remaining == 0)
            {
                if (m_state.block_type == LzxConstants.BLOCKTYPE.UNCOMPRESSED)
                {
                    if ((m_state.block_length & 1) == 1)
                    {
                        inData.ReadByte();
                    }
                    bitBuffer.InitBitStream();
                }
                m_state.block_type = (LzxConstants.BLOCKTYPE)bitBuffer.ReadBits(3);
                uint num7 = bitBuffer.ReadBits(16);
                uint num8 = bitBuffer.ReadBits(8);
                m_state.block_remaining = (m_state.block_length = (num7 << 8) | num8);
                switch (m_state.block_type)
                {
                    case LzxConstants.BLOCKTYPE.ALIGNED:
                        num7 = 0u;
                        num8 = 0u;
                        for (; num7 < 8; num7++)
                        {
                            num8 = bitBuffer.ReadBits(3);
                            m_state.ALIGNED_len[num7] = (byte)num8;
                        }
                        MakeDecodeTable(8u, 7u, m_state.ALIGNED_len, m_state.ALIGNED_table);
                        goto case LzxConstants.BLOCKTYPE.VERBATIM;
                    case LzxConstants.BLOCKTYPE.VERBATIM:
                        ReadLengths(m_state.MAINTREE_len, 0u, 256u, bitBuffer);
                        ReadLengths(m_state.MAINTREE_len, 256u, m_state.main_elements, bitBuffer);
                        MakeDecodeTable(656u, 12u, m_state.MAINTREE_len, m_state.MAINTREE_table);
                        if (m_state.MAINTREE_len[232] != 0)
                        {
                            m_state.intel_started = 1;
                        }
                        ReadLengths(m_state.LENGTH_len, 0u, 249u, bitBuffer);
                        MakeDecodeTable(250u, 12u, m_state.LENGTH_len, m_state.LENGTH_table);
                        break;
                    case LzxConstants.BLOCKTYPE.UNCOMPRESSED:
                        m_state.intel_started = 1;
                        bitBuffer.EnsureBits(16);
                        if (bitBuffer.GetBitsLeft() > 16)
                        {
                            inData.Seek(-2L, SeekOrigin.Current);
                        }
                        byte num9 = (byte)inData.ReadByte();
                        byte b = (byte)inData.ReadByte();
                        byte b2 = (byte)inData.ReadByte();
                        byte b3 = (byte)inData.ReadByte();
                        num3 = (uint)(num9 | (b << 8) | (b2 << 16) | (b3 << 24));
                        byte num10 = (byte)inData.ReadByte();
                        b = (byte)inData.ReadByte();
                        b2 = (byte)inData.ReadByte();
                        b3 = (byte)inData.ReadByte();
                        num4 = (uint)(num10 | (b << 8) | (b2 << 16) | (b3 << 24));
                        byte num11 = (byte)inData.ReadByte();
                        b = (byte)inData.ReadByte();
                        b2 = (byte)inData.ReadByte();
                        b3 = (byte)inData.ReadByte();
                        num5 = (uint)(num11 | (b << 8) | (b2 << 16) | (b3 << 24));
                        break;
                    default:
                        return -1;
                }
            }
            if (inData.Position > position + inLen && (inData.Position > position + inLen + 2 || bitBuffer.GetBitsLeft() < 16))
            {
                return -1;
            }
            int num12;
            while ((num12 = (int)m_state.block_remaining) > 0 && num6 > 0)
            {
                if (num12 > num6)
                {
                    num12 = num6;
                }
                num6 -= num12;
                m_state.block_remaining -= (uint)num12;
                num2 &= window_size - 1;
                if (num2 + num12 > window_size)
                {
                    return -1;
                }
                switch (m_state.block_type)
                {
                    case LzxConstants.BLOCKTYPE.VERBATIM:
                        while (num12 > 0)
                        {
                            int num13 = (int)ReadHuffSym(m_state.MAINTREE_table, m_state.MAINTREE_len, 656u, 12u, bitBuffer);
                            if (num13 < 256)
                            {
                                window[num2++] = (byte)num13;
                                num12--;
                                continue;
                            }
                            num13 -= 256;
                            int num14 = num13 & 7;
                            if (num14 == 7)
                            {
                                int num15 = (int)ReadHuffSym(m_state.LENGTH_table, m_state.LENGTH_len, 250u, 12u, bitBuffer);
                                num14 += num15;
                            }
                            num14 += 2;
                            int num16 = num13 >> 3;
                            if (num16 > 2)
                            {
                                if (num16 != 3)
                                {
                                    int num17 = extra_bits[num16];
                                    int num18 = (int)bitBuffer.ReadBits((byte)num17);
                                    num16 = (int)(position_base[num16] - 2) + num18;
                                }
                                else
                                {
                                    num16 = 1;
                                }
                                num5 = num4;
                                num4 = num3;
                                num3 = (uint)num16;
                            }
                            else
                            {
                                switch (num16)
                                {
                                    case 0:
                                        num16 = (int)num3;
                                        break;
                                    case 1:
                                        num16 = (int)num4;
                                        num4 = num3;
                                        num3 = (uint)num16;
                                        break;
                                    default:
                                        num16 = (int)num5;
                                        num5 = num3;
                                        num3 = (uint)num16;
                                        break;
                                }
                            }
                            int num20 = (int)num2;
                            num12 -= num14;
                            int num21;
                            if (num2 >= num16)
                            {
                                num21 = num20 - num16;
                            }
                            else
                            {
                                num21 = num20 + ((int)window_size - num16);
                                int num22 = num16 - (int)num2;
                                if (num22 < num14)
                                {
                                    num14 -= num22;
                                    num2 += (uint)num22;
                                    while (num22-- > 0)
                                    {
                                        window[num20++] = window[num21++];
                                    }
                                    num21 = 0;
                                }
                            }
                            num2 += (uint)num14;
                            while (num14-- > 0)
                            {
                                window[num20++] = window[num21++];
                            }
                        }
                        break;
                    case LzxConstants.BLOCKTYPE.ALIGNED:
                        while (num12 > 0)
                        {
                            int num13 = (int)ReadHuffSym(m_state.MAINTREE_table, m_state.MAINTREE_len, 656u, 12u, bitBuffer);
                            if (num13 < 256)
                            {
                                window[num2++] = (byte)num13;
                                num12--;
                                continue;
                            }
                            num13 -= 256;
                            int num14 = num13 & 7;
                            if (num14 == 7)
                            {
                                int num15 = (int)ReadHuffSym(m_state.LENGTH_table, m_state.LENGTH_len, 250u, 12u, bitBuffer);
                                num14 += num15;
                            }
                            num14 += 2;
                            int num16 = num13 >> 3;
                            if (num16 > 2)
                            {
                                int num17 = extra_bits[num16];
                                num16 = (int)(position_base[num16] - 2);
                                if (num17 > 3)
                                {
                                    num17 -= 3;
                                    int num18 = (int)bitBuffer.ReadBits((byte)num17);
                                    num16 += num18 << 3;
                                    int num19 = (int)ReadHuffSym(m_state.ALIGNED_table, m_state.ALIGNED_len, 8u, 7u, bitBuffer);
                                    num16 += num19;
                                }
                                else if (num17 == 3)
                                {
                                    int num19 = (int)ReadHuffSym(m_state.ALIGNED_table, m_state.ALIGNED_len, 8u, 7u, bitBuffer);
                                    num16 += num19;
                                }
                                else if (num17 > 0)
                                {
                                    int num18 = (int)bitBuffer.ReadBits((byte)num17);
                                    num16 += num18;
                                }
                                else
                                {
                                    num16 = 1;
                                }
                                num5 = num4;
                                num4 = num3;
                                num3 = (uint)num16;
                            }
                            else
                            {
                                switch (num16)
                                {
                                    case 0:
                                        num16 = (int)num3;
                                        break;
                                    case 1:
                                        num16 = (int)num4;
                                        num4 = num3;
                                        num3 = (uint)num16;
                                        break;
                                    default:
                                        num16 = (int)num5;
                                        num5 = num3;
                                        num3 = (uint)num16;
                                        break;
                                }
                            }
                            int num20 = (int)num2;
                            num12 -= num14;
                            int num21;
                            if (num2 >= num16)
                            {
                                num21 = num20 - num16;
                            }
                            else
                            {
                                num21 = num20 + ((int)window_size - num16);
                                int num22 = num16 - (int)num2;
                                if (num22 < num14)
                                {
                                    num14 -= num22;
                                    num2 += (uint)num22;
                                    while (num22-- > 0)
                                    {
                                        window[num20++] = window[num21++];
                                    }
                                    num21 = 0;
                                }
                            }
                            num2 += (uint)num14;
                            while (num14-- > 0)
                            {
                                window[num20++] = window[num21++];
                            }
                        }
                        break;
                    case LzxConstants.BLOCKTYPE.UNCOMPRESSED:
                        if (inData.Position + num12 > num)
                        {
                            return -1;
                        }
                        byte[] array = new byte[num12];
                        int read = inData.ReadAtLeast(array, num12, throwOnEndOfStream: false);
                        if (read < num12) return -1;
                        array.CopyTo(window, (int)num2);
                        num2 += (uint)num12;
                        break;
                    default:
                        return -1;
                }
            }
        }
        if (num6 != 0)
        {
            return -1;
        }
        int num23 = (int)num2;
        if (num23 == 0)
        {
            num23 = (int)window_size;
        }
        num23 -= outLen;
        outData.Write(window, num23, outLen);
        m_state.window_posn = num2;
        m_state.R0 = num3;
        m_state.R1 = num4;
        m_state.R2 = num5;
        if (m_state.frames_read++ < 32768 && m_state.intel_filesize != 0)
        {
            if (outLen <= 6 || m_state.intel_started == 0)
            {
                m_state.intel_curpos += outLen;
            }
            else
            {
                int num24 = outLen - 10;
                uint num25 = (uint)m_state.intel_curpos;
                m_state.intel_curpos = (int)num25 + outLen;
                while (outData.Position < num24)
                {
                    if (outData.ReadByte() != 232)
                    {
                        num25++;
                    }
                }
            }
            return -1;
        }
        return 0;
    }

    private int MakeDecodeTable(uint nsyms, uint nbits, byte[] length, ushort[] table)
    {
        byte b = 1;
        uint num = 0u;
        uint num2 = (uint)(1 << (int)nbits);
        uint num3 = num2 >> 1;
        uint num4 = num3;
        while (b <= nbits)
        {
            for (ushort num5 = 0; num5 < nsyms; num5++)
            {
                if (length[num5] == b)
                {
                    uint num6 = num;
                    if ((num += num3) > num2)
                    {
                        return 1;
                    }
                    uint num7 = num3;
                    while (num7-- != 0)
                    {
                        table[num6++] = num5;
                    }
                }
            }
            num3 >>= 1;
            b++;
        }
        if (num != num2)
        {
            for (ushort num5 = (ushort)num; num5 < num2; num5++)
            {
                table[num5] = 0;
            }
            num <<= 16;
            num2 <<= 16;
            num3 = 32768u;
            while (b <= 16)
            {
                for (ushort num5 = 0; num5 < nsyms; num5++)
                {
                    if (length[num5] == b)
                    {
                        uint num6 = num >> 16;
                        for (uint num7 = 0u; num7 < b - nbits; num7++)
                        {
                            if (table[num6] == 0)
                            {
                                table[num4 << 1] = 0;
                                table[(num4 << 1) + 1] = 0;
                                table[num6] = (ushort)num4++;
                            }
                            num6 = (uint)(table[num6] << 1);
                            if (((num >> (int)(15 - num7)) & 1) == 1)
                            {
                                num6++;
                            }
                        }
                        table[num6] = num5;
                        if ((num += num3) > num2)
                        {
                            return 1;
                        }
                    }
                }
                num3 >>= 1;
                b++;
            }
        }
        if (num == num2)
        {
            return 0;
        }
        for (ushort num5 = 0; num5 < nsyms; num5++)
        {
            if (length[num5] != 0)
            {
                return 1;
            }
        }
        return 0;
    }

    private void ReadLengths(byte[] lens, uint first, uint last, BitBuffer bitbuf)
    {
        uint num;
        for (num = 0u; num < 20; num++)
        {
            uint num2 = bitbuf.ReadBits(4);
            m_state.PRETREE_len[num] = (byte)num2;
        }
        MakeDecodeTable(20u, 6u, m_state.PRETREE_len, m_state.PRETREE_table);
        num = first;
        while (num < last)
        {
            int num3 = (int)ReadHuffSym(m_state.PRETREE_table, m_state.PRETREE_len, 20u, 6u, bitbuf);
            switch (num3)
            {
                case 17:
                {
                    uint num2 = bitbuf.ReadBits(4);
                    num2 += 4;
                    while (num2-- != 0)
                    {
                        lens[num++] = 0;
                    }
                    break;
                }
                case 18:
                {
                    uint num2 = bitbuf.ReadBits(5);
                    num2 += 20;
                    while (num2-- != 0)
                    {
                        lens[num++] = 0;
                    }
                    break;
                }
                case 19:
                {
                    uint num2 = bitbuf.ReadBits(1);
                    num2 += 4;
                    num3 = (int)ReadHuffSym(m_state.PRETREE_table, m_state.PRETREE_len, 20u, 6u, bitbuf);
                    num3 = lens[num] - num3;
                    if (num3 < 0)
                    {
                        num3 += 17;
                    }
                    while (num2-- != 0)
                    {
                        lens[num++] = (byte)num3;
                    }
                    break;
                }
                default:
                    num3 = lens[num] - num3;
                    if (num3 < 0)
                    {
                        num3 += 17;
                    }
                    lens[num++] = (byte)num3;
                    break;
            }
        }
    }

    private uint ReadHuffSym(ushort[] table, byte[] lengths, uint nsyms, uint nbits, BitBuffer bitbuf)
    {
        bitbuf.EnsureBits(16);
        uint num;
        uint num2;
        if ((num = table[bitbuf.PeekBits((byte)nbits)]) >= nsyms)
        {
            num2 = (uint)(1 << (int)(32 - nbits));
            do
            {
                num2 >>= 1;
                num <<= 1;
                num |= (((bitbuf.GetBuffer() & num2) != 0) ? 1u : 0u);
                if (num2 == 0)
                {
                    return 0u;
                }
            }
            while ((num = table[num]) >= nsyms);
        }
        num2 = lengths[num];
        bitbuf.RemoveBits((byte)num2);
        return num;
    }
}

internal struct LzxConstants
{
    public enum BLOCKTYPE
    {
        INVALID,
        VERBATIM,
        ALIGNED,
        UNCOMPRESSED
    }
}

internal sealed class Lz4DecoderStream : Stream
{
    private enum DecodePhase
    {
        ReadToken,
        ReadExLiteralLength,
        CopyLiteral,
        ReadOffset,
        ReadExMatchLength,
        CopyMatch
    }

    private long inputLength = long.MaxValue;
    private Stream input = null!; // 由 Reset/有参构造函数初始化，使用前已校验 != null
    private const int DecBufLen = 65536;
    private const int DecBufMask = 65535;
    private const int InBufLen = 128;
    private byte[] decodeBuffer = new byte[65664];
    private int decodeBufferPos;
    private int inBufPos;
    private int inBufEnd;
    private DecodePhase phase;
    private int litLen;
    private int matLen;
    private int matDst;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public Lz4DecoderStream() { }

    public Lz4DecoderStream(Stream input, long inputLength = long.MaxValue)
    {
        Reset(input, inputLength);
    }

    public void Reset(Stream input, long inputLength = long.MaxValue)
    {
        this.inputLength = inputLength;
        this.input = input;
        phase = DecodePhase.ReadToken;
        decodeBufferPos = 0;
        litLen = 0;
        matLen = 0;
        matDst = 0;
        inBufPos = 65536;
        inBufEnd = 65536;
    }

    protected override void Dispose(bool disposing)
    {
        input = null!;
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }
        if (offset < 0 || count < 0 || buffer.Length - count < offset)
        {
            throw new ArgumentOutOfRangeException();
        }
        if (input == null)
        {
            throw new InvalidOperationException();
        }
        int num = count;
        byte[] array = decodeBuffer;
        int num7;
        switch (phase)
        {
            default:
            {
                int num2;
                if (inBufPos < inBufEnd)
                {
                    num2 = array[inBufPos++];
                }
                else
                {
                    num2 = ReadByteCore();
                    if (num2 == -1)
                    {
                        break;
                    }
                }
                litLen = num2 >> 4;
                matLen = (num2 & 0xF) + 4;
                int num3 = litLen;
                if (num3 != 0)
                {
                    if (num3 == 15)
                    {
                        phase = DecodePhase.ReadExLiteralLength;
                        goto case DecodePhase.ReadExLiteralLength;
                    }
                    phase = DecodePhase.CopyLiteral;
                    goto case DecodePhase.CopyLiteral;
                }
                phase = DecodePhase.ReadOffset;
                goto case DecodePhase.ReadOffset;
            }
            case DecodePhase.ReadExLiteralLength:
                while (true)
                {
                    int num14;
                    if (inBufPos < inBufEnd)
                    {
                        num14 = array[inBufPos++];
                    }
                    else
                    {
                        num14 = ReadByteCore();
                        if (num14 == -1)
                        {
                            break;
                        }
                    }
                    litLen += num14;
                    if (num14 == 255)
                    {
                        continue;
                    }
                    goto IL_012e;
                }
                break;
            case DecodePhase.CopyLiteral:
                do
                {
                    int num4 = ((litLen < num) ? litLen : num);
                    if (num4 == 0)
                    {
                        break;
                    }
                    if (inBufPos + num4 <= inBufEnd)
                    {
                        int num5 = offset;
                        int num6 = num4;
                        while (num6-- != 0)
                        {
                            buffer[num5++] = array[inBufPos++];
                        }
                        num7 = num4;
                    }
                    else
                    {
                        num7 = ReadCore(buffer, offset, num4);
                        if (num7 == 0)
                        {
                            goto end_IL_0045;
                        }
                    }
                    offset += num7;
                    num -= num7;
                    litLen -= num7;
                }
                while (litLen != 0);
                if (num == 0)
                {
                    break;
                }
                phase = DecodePhase.ReadOffset;
                goto case DecodePhase.ReadOffset;
            case DecodePhase.ReadOffset:
                if (inBufPos + 1 < inBufEnd)
                {
                    matDst = (array[inBufPos + 1] << 8) | array[inBufPos];
                    inBufPos += 2;
                }
                else
                {
                    matDst = ReadOffsetCore();
                    if (matDst == -1)
                    {
                        break;
                    }
                }
                if (matLen == 19)
                {
                    phase = DecodePhase.ReadExMatchLength;
                    goto case DecodePhase.ReadExMatchLength;
                }
                phase = DecodePhase.CopyMatch;
                goto case DecodePhase.CopyMatch;
            case DecodePhase.ReadExMatchLength:
                while (true)
                {
                    int num13;
                    if (inBufPos < inBufEnd)
                    {
                        num13 = array[inBufPos++];
                    }
                    else
                    {
                        num13 = ReadByteCore();
                        if (num13 == -1)
                        {
                            break;
                        }
                    }
                    matLen += num13;
                    if (num13 == 255)
                    {
                        continue;
                    }
                    goto IL_0293;
                }
                break;
            case DecodePhase.CopyMatch:
            {
                int num8 = ((matLen < num) ? matLen : num);
                if (num8 != 0)
                {
                    num7 = count - num;
                    int num9 = matDst - num7;
                    if (num9 > 0)
                    {
                        int num10 = decodeBufferPos - num9;
                        if (num10 < 0)
                        {
                            num10 += 65536;
                        }
                        int num11 = ((num9 < num8) ? num9 : num8);
                        while (num11-- != 0)
                        {
                            buffer[offset++] = array[num10++ & 0xFFFF];
                        }
                    }
                    else
                    {
                        num9 = 0;
                    }
                    int num12 = offset - matDst;
                    for (int i = num9; i < num8; i++)
                    {
                        buffer[offset++] = buffer[num12++];
                    }
                    num -= num8;
                    matLen -= num8;
                }
                if (num == 0)
                {
                    break;
                }
                phase = DecodePhase.ReadToken;
                goto default;
            }
            IL_0293:
            phase = DecodePhase.CopyMatch;
            goto case DecodePhase.CopyMatch;
            IL_012e:
            phase = DecodePhase.CopyLiteral;
            goto case DecodePhase.CopyLiteral;
            end_IL_0045:
            break;
        }
        num7 = count - num;
        int num15 = ((num7 < 65536) ? num7 : 65536);
        int srcOffset = offset - num15;
        if (num15 == 65536)
        {
            Buffer.BlockCopy(buffer, srcOffset, array, 0, 65536);
            decodeBufferPos = 0;
        }
        else
        {
            int num16 = decodeBufferPos;
            while (num15-- != 0)
            {
                array[num16++ & 0xFFFF] = buffer[srcOffset++];
            }
            decodeBufferPos = num16 & 0xFFFF;
        }
        return num7;
    }

    private int ReadByteCore()
    {
        byte[] array = decodeBuffer;
        if (inBufPos == inBufEnd)
        {
            int num = input.Read(array, 65536, (int)((128 < inputLength) ? 128 : inputLength));
            if (num == 0)
            {
                return -1;
            }
            inputLength -= num;
            inBufPos = 65536;
            inBufEnd = 65536 + num;
        }
        return array[inBufPos++];
    }

    private int ReadOffsetCore()
    {
        byte[] array = decodeBuffer;
        if (inBufPos == inBufEnd)
        {
            int num = input.Read(array, 65536, (int)((128 < inputLength) ? 128 : inputLength));
            if (num == 0)
            {
                return -1;
            }
            inputLength -= num;
            inBufPos = 65536;
            inBufEnd = 65536 + num;
        }
        if (inBufEnd - inBufPos == 1)
        {
            array[65536] = array[inBufPos];
            int num2 = input.Read(array, 65537, (int)((127 < inputLength) ? 127 : inputLength));
            if (num2 == 0)
            {
                inBufPos = 65536;
                inBufEnd = 65537;
                return -1;
            }
            inputLength -= num2;
            inBufPos = 65536;
            inBufEnd = 65536 + num2 + 1;
        }
        int result = (array[inBufPos + 1] << 8) | array[inBufPos];
        inBufPos += 2;
        return result;
    }

    private int ReadCore(byte[] buffer, int offset, int count)
    {
        int num = count;
        byte[] array = decodeBuffer;
        int num2 = inBufEnd - inBufPos;
        int num3 = ((num < num2) ? num : num2);
        if (num3 != 0)
        {
            int num4 = inBufPos;
            int num5 = num3;
            while (num5-- != 0)
            {
                buffer[offset++] = array[num4++];
            }
            inBufPos = num4;
            num -= num3;
        }
        if (num != 0)
        {
            int num6;
            if (num >= 128)
            {
                num6 = input.Read(buffer, offset, (int)((num < inputLength) ? num : inputLength));
                num -= num6;
            }
            else
            {
                num6 = input.Read(array, 65536, (int)((128 < inputLength) ? 128 : inputLength));
                inBufPos = 65536;
                inBufEnd = 65536 + num6;
                num3 = ((num < num6) ? num : num6);
                int num7 = inBufPos;
                int num8 = num3;
                while (num8-- != 0)
                {
                    buffer[offset++] = array[num7++];
                }
                inBufPos = num7;
                num -= num3;
            }
            inputLength -= num6;
        }
        return count - num;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
