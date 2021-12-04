using Force.Crc32;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace decodl;

public unsafe class PNGEncoder
{
    byte* Raw;
    uint Width;
    uint Height;

    byte CompressionMethod = 0;
    byte FilterMethod = 0;
    byte InterlaceMethod = 0;

    public ColorTypes ColorType = ColorTypes.RGBA;
    public byte BitDepth = 8;

    public bool AdaptiveFiltering = true;
    public FilterType? FixedFilter = null;

    public byte[] FiltersApplied { get; protected set; }

    /// <summary>
    /// Initializes a PNG encoder with a pointer to a pixel array.
    /// </summary>
    /// <param name="Bytes">The raw RGBA pixel data.</param>
    public PNGEncoder(byte* SourceBytes, uint Width, uint Height)
    {
        this.Raw = SourceBytes;
        this.Width = Width;
        this.Height = Height;
    }

    public void Encode(string Filename)
    {
        // Apply adaptive filtering on source data
        byte[] filtered = ColorType switch
        {
            ColorTypes.RGBA => EncodeRGBA(BitDepth),
            _ => throw new PNGException("Unsupported color type.")
        };

        // Compress filtered data
        MemoryStream datastream = Compress(filtered);

        // Start writing data
        BinaryWriter bw = new BinaryWriter(File.Create(Filename));

        // Write PNG signature
        bw.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        
        // Write IHDR chunk
        PNGHeaderChunk header = new PNGHeaderChunk(bw, Width, Height, BitDepth, (byte) ColorType, CompressionMethod, FilterMethod, InterlaceMethod);
        header.Write();
        header.Dispose();

        // Write IDAT chunk
        PNGDataChunk data = new PNGDataChunk(bw, datastream);
        data.Write();
        data.Dispose();

        // Write IEND chunk
        PNGEndChunk end = new PNGEndChunk(bw);
        end.Write();
        end.Dispose();

        bw.Dispose();
    }

    MemoryStream Compress(byte[] data)
    {
        MemoryStream cstream = new MemoryStream();
        cstream.WriteByte(120); // LZ77 window size and compression method
        cstream.WriteByte(1); // Additional flag
        // Optimal compression
        DeflateStream deflate = new DeflateStream(cstream, CompressionLevel.Optimal, true);
        // Write to cstream
        deflate.Write(data, 0, data.Length);
        deflate.Close();
        // Calculate adler checksum over uncompressed data
        uint adler = CalculateAdler(data);
        // Write checksum to compressed datastream
        cstream.Write(UInt32ToBE(adler), 0, 4);
        deflate.Dispose();
        cstream.Seek(0, SeekOrigin.Begin);
        return cstream;
    }

    byte[] EncodeRGBA(int BitDepth)
    {
        return BitDepth switch
        {
            8 => EncodeRGBA8(),
            _ => throw new PNGException("Unsupported bit depth."),
        };
    }

    byte[] EncodeRGBA8()
    {
        byte[] Bytes = new byte[Height * (Width * 4 + 1)];
        FiltersApplied = new byte[Height];
        for (int y = 0; y < Height; y++)
        {
            byte filter = 0;
            int min = int.MaxValue;
            if (AdaptiveFiltering)
            {
                for (byte i = 0; i < 5; i++)
                {
                    int total = 0;
                    for (int x = 0; x < Width; x++)
                    {
                        for (int rgba = 0; rgba < 4; rgba++)
                        {
                            uint srcidx = (uint) (y * Width * 4 + x * 4 + rgba);
                            uint dstidx = (uint) (y * (Width * 4 + 1) + 1 + x * 4 + rgba);
                            byte Left = x == 0 ? (byte) 0 : Raw[srcidx - 4];
                            byte Up = y == 0 ? (byte) 0 : Raw[srcidx - Width * 4];
                            byte LeftUp = x == 0 || y == 0 ? (byte) 0 : Raw[srcidx - Width * 4 - 4];
                            total += Filter(Raw[srcidx], Left, Up, LeftUp, i);
                        }
                    }
                    if (total < min)
                    {
                        filter = i;
                        min = total;
                    }
                }
            }
            else
            {
                if (FixedFilter == null) throw new PNGException("Cannot apply fixed filter when none is set.");
                filter = (byte) FixedFilter;
            }
            FiltersApplied[y] = filter;
            Bytes[y * (Width * 4 + 1)] = filter;
            for (int x = 0; x < Width; x++)
            {
                for (int rgba = 0; rgba < 4; rgba++)
                {
                    uint srcidx = (uint) (y * Width * 4 + x * 4 + rgba);
                    uint dstidx = (uint) (y * (Width * 4 + 1) + 1 + x * 4 + rgba);
                    byte Left = x == 0 ? (byte) 0 : Raw[srcidx - 4];
                    byte Up = y == 0 ? (byte) 0 : Raw[srcidx - Width * 4];
                    byte LeftUp = x == 0 || y == 0 ? (byte)0 : Raw[srcidx - Width * 4 - 4];
                    Bytes[dstidx] = Filter(Raw[srcidx], Left, Up, LeftUp, filter);
                }
            }
        }
        return Bytes;
    }

    byte Filter(byte Raw, byte Left, byte Up, byte LeftUp, byte filter)
    {
        if (filter == 0) return Raw;
        else if (filter == 1)
        {
            return (byte) ((Raw - Left) % 256);
        }
        else if (filter == 2)
        {
            return (byte) ((Raw - Up) % 256);
        }
        else if (filter == 3)
        {
            return (byte) ((Raw - (Left + Up) / 2) % 256);
        }
        else if (filter == 4)
        {
            int p = Left + Up - LeftUp;
            int pL = Math.Abs(p - Left);
            int pU = Math.Abs(p - Up);
            int pLU = Math.Abs(p - LeftUp);
            if (pL <= pU && pL <= pLU) return (byte) ((Raw - Left) % 256);
            else if (pU <= pLU) return (byte) ((Raw - Up) % 256);
            else return (byte) ((Raw - LeftUp) % 256);
        }
        throw new PNGException("Invalid filter type encountered.");
    }

    static byte[] UInt32ToBE(uint n)
    {
        byte[] data = BitConverter.GetBytes(n);
        if (BitConverter.IsLittleEndian) Array.Reverse(data);
        return data;
    }

    static uint CalculateCRC(byte[] Data)
    {
        return Crc32Algorithm.Compute(Data);
    }

    static uint CalculateAdler(byte[] Data)
    {
        const int mod = 65521;
        uint a = 1, b = 0;
        foreach (byte x in Data)
        {
            a = (a + (byte) x) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }

    class PNGChunk : IDisposable
    {
        private BinaryWriter FileWriter;

        protected BinaryWriter DataWriter;
        protected MemoryStream DataStream;

        public PNGChunk(string ChunkType, BinaryWriter FileWriter)
        {
            this.FileWriter = FileWriter;
            this.DataStream = new MemoryStream();
            this.DataWriter = new BinaryWriter(DataStream);
            this.DataWriter.Write(ChunkType.ToCharArray());
        }

        public virtual void Write()
        {
            byte[] Data = DataStream.ToArray();
            FileWriter.Write(UInt32ToBE((uint) Data.Length - 4)); // Data length - 4 header bytes
            FileWriter.Write(Data);
            FileWriter.Write(UInt32ToBE(Crc32Algorithm.Compute(Data)));
        }

        public void Dispose()
        {
            DataStream.Dispose();
            DataWriter.Dispose();
        }
    }

    class PNGHeaderChunk : PNGChunk
    {
        public PNGHeaderChunk(BinaryWriter FileWriter, uint Width, uint Height,
            byte BitDepth, byte ColorType, byte CompressionMethod,
            byte FilterMethod, byte InterlaceMethod)
            : base("IHDR", FileWriter)
        {
            DataWriter.Write(UInt32ToBE(Width));
            DataWriter.Write(UInt32ToBE(Height));
            DataWriter.Write(BitDepth);
            DataWriter.Write(ColorType);
            DataWriter.Write(CompressionMethod);
            DataWriter.Write(FilterMethod);
            DataWriter.Write(InterlaceMethod);
        }
    }

    class PNGDataChunk : PNGChunk
    {
        public PNGDataChunk(BinaryWriter FileWriter, MemoryStream Data)
            : base("IDAT", FileWriter)
        {
            Data.CopyTo(DataStream);
        }
    }

    class PNGEndChunk : PNGChunk
    {
        public PNGEndChunk(BinaryWriter FileWriter) : base("IEND", FileWriter) { }
    }
}