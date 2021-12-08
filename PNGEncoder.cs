using Force.Crc32;
using System;
using System.Collections.Generic;
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

    List<Color> Palette = new List<Color>();
    Dictionary<Color, Color> PaletteReductions = new Dictionary<Color, Color>();

    /// <summary>
    /// Whether to read the input data as RGBA or as ABGR.
    /// Use this switch, rather than reorganising your input data.
    /// </summary>
    public bool InvertData = false;

    /// <summary>
    /// The color type in which to encode the final image.
    /// </summary>
    public ColorTypes ColorType = ColorTypes.RGBA;

    /// <summary>
    /// The number of bits per sample. A value of 8 means 255 values per color channel.
    /// </summary>
    public byte BitDepth = 0;

    /// <summary>
    /// Whether to use a different filter for every different scanline.
    /// If disabled, you must specify a value for FixedFilter.
    /// </summary>
    public bool AdaptiveFiltering = true;

    /// <summary>
    /// If adaptive filtering is disabled, this filter will be used for all scanlines instead.
    /// </summary>
    public FilterType? FixedFilter = null;

    /// <summary>
    /// Override the maximum size of the palette of an indexed image. Note that this cannot be larger than 2^BitDepth - 1.
    /// If set to 0, the maximum is automatically set to 2^BitDepth - 1.
    /// </summary>
    public int MaxPaletteSize = 0;

    /// <summary>
    /// <para>If true, images that use too many colors and cannot be indexed will have their palette
    /// reduced until it does fit, meaning some colors will be lost. It is important to note that this is a slow process.</para>
    /// <para>If false, the encoder will raise a PNGException.</para>
    /// </summary>
    public bool ReduceUnindexableImages = false;

    /// <summary>
    /// If true, will include transparency information in the PNG.
    /// If false, no transparency will be retained in the PNG.
    /// </summary>
    public bool IncludeIndexedTransparency = true;

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
        if (!AdaptiveFiltering && FixedFilter == null) throw new PNGException("If adaptive filtering is disabled, a fixed filter must be set.");

        // Apply filtering on source data
        byte[] filtered = ColorType switch
        {
            ColorTypes.RGBA => EncodeRGBA(BitDepth),
            ColorTypes.RGB => EncodeRGB(BitDepth),
            ColorTypes.Indexed => EncodePLTE(BitDepth),
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

        if (ColorType == ColorTypes.Indexed)
        {
            // Write PLTE chunk
            PNGPaletteChunk plte = new PNGPaletteChunk(bw, Palette);
            plte.Write();
            plte.Dispose();

            if (IncludeIndexedTransparency)
            {
                // Write tRNS chunk
                PNGTransparencyChunk trns = new PNGTransparencyChunk(bw, Palette);
                trns.Write();
                trns.Dispose();
            }
        }

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
        if (BitDepth == 0) BitDepth = this.BitDepth = 8;
        return BitDepth switch
        {
            8 => EncodeRGBA8(),
            _ => throw new PNGException("Unsupported bit depth."),
        };
    }

    byte[] EncodeRGBA8()
    {
        byte[] Bytes = new byte[Height * (Width * 4 + 1)];
        for (int y = 0; y < Height; y++)
        {
            byte filter = 0;
            int min = int.MaxValue;
            if (AdaptiveFiltering)
            {
                // Do not test filter cost for None as at least one of the 4 other filters will almost certainly perform better
                for (byte i = 1; i < 5; i++)
                {
                    int total = 0;
                    for (int x = 0; x < Width; x++)
                    {
                        for (int rgba = 0; rgba < 4; rgba++)
                        {
                            uint srcidx = (uint) (y * Width * 4 + x * 4 + (InvertData ? 3 - rgba : rgba));
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
                filter = (byte) FixedFilter;
            }
            Bytes[y * (Width * 4 + 1)] = filter;
            for (int x = 0; x < Width; x++)
            {
                for (int rgba = 0; rgba < 4; rgba++)
                {
                    uint srcidx = (uint) (y * Width * 4 + x * 4 + (InvertData ? 3 - rgba : rgba));
                    uint dstidx = (uint) (y * (Width * 4 + 1) + 1 + x * 4 + rgba);
                    byte Left = x == 0 ? (byte) 0 : Raw[srcidx - 4];
                    byte Up = y == 0 ? (byte) 0 : Raw[srcidx - Width * 4];
                    byte LeftUp = x == 0 || y == 0 ? (byte) 0 : Raw[srcidx - Width * 4 - 4];
                    Bytes[dstidx] = Filter(Raw[srcidx], Left, Up, LeftUp, filter);
                }
            }
        }
        return Bytes;
    }

    byte[] EncodeRGB(int BitDepth)
    {
        if (BitDepth == 0) BitDepth = this.BitDepth = 8;
        return BitDepth switch
        {
            8 => EncodeRGB8(),
            _ => throw new PNGException("Unsupported bit depth."),
        };
    }

    byte[] EncodeRGB8()
    {
        byte[] Bytes = new byte[Height * (Width * 3 + 1)];
        for (int y = 0; y < Height; y++)
        {
            byte filter = 0;
            int min = int.MaxValue;
            if (AdaptiveFiltering)
            {
                // Do not test filter cost for None as at least one of the 4 other filters will almost certainly perform better
                for (byte i = 1; i < 5; i++)
                {
                    int total = 0;
                    for (int x = 0; x < Width; x++)
                    {
                        for (int rgba = 0; rgba < 3; rgba++)
                        {
                            uint srcidx = (uint) (y * Width * 4 + x * 4 + (InvertData ? 3 - rgba : rgba));
                            uint dstidx = (uint) (y * (Width * 3 + 1) + 1 + x * 3 + rgba);
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
                filter = (byte) FixedFilter;
            }
            Bytes[y * (Width * 3 + 1)] = filter;
            for (int x = 0; x < Width; x++)
            {
                for (int rgba = 0; rgba < 3; rgba++)
                {
                    uint srcidx = (uint) (y * Width * 4 + x * 4 + (InvertData ? 3 - rgba : rgba));
                    uint dstidx = (uint) (y * (Width * 3 + 1) + 1 + x * 3 + rgba);
                    byte Left = x == 0 ? (byte) 0 : Raw[srcidx - 4];
                    byte Up = y == 0 ? (byte) 0 : Raw[srcidx - Width * 4];
                    byte LeftUp = x == 0 || y == 0 ? (byte) 0 : Raw[srcidx - Width * 4 - 4];
                    Bytes[dstidx] = Filter(Raw[srcidx], Left, Up, LeftUp, filter);
                }
            }
        }
        return Bytes;
    }

    void CreatePalette(int BitDepth)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte r, g, b, a;
                if (InvertData)
                {
                    r = Raw[x * 4 + y * Width * 4 + 3];
                    g = Raw[x * 4 + y * Width * 4 + 2];
                    b = Raw[x * 4 + y * Width * 4 + 1];
                    a = Raw[x * 4 + y * Width * 4];
                }
                else
                {
                    r = Raw[x * 4 + y * Width * 4];
                    g = Raw[x * 4 + y * Width * 4 + 1];
                    b = Raw[x * 4 + y * Width * 4 + 2];
                    a = Raw[x * 4 + y * Width * 4 + 3];
                }
                Color c = new Color(r, g, b, a);
                if (!Palette.Contains(c)) Palette.Add(c);
            }
        }
        if (BitDepth == 0)
        {
            if (Palette.Count <= 2) BitDepth = 1;
            else if (Palette.Count <= 4) BitDepth = 2;
            else if (Palette.Count <= 16) BitDepth = 4;
            else BitDepth = 8;
            this.BitDepth = (byte) BitDepth;
        }
        int max = (int) Math.Pow(2, BitDepth) - 1;
        if (MaxPaletteSize != 0)
        {
            if (MaxPaletteSize > 256) throw new PNGException("The palette cannot have more than 256 entries.");
            else if (MaxPaletteSize < max) max = MaxPaletteSize;
            else throw new PNGException($"The palette can have at most {max} entries at a bit depth of {BitDepth}.");
        }
        if (Palette.Count > max)
        {
            if (ReduceUnindexableImages)
            {
                ReducePalette(max);
            }
            else
            {
                throw new PNGException("Unable to index image as it uses too many colors. Did you mean to set the ReduceUnindexableImages flag to true?");
            }
        }
    }

    /// <summary>
    /// Reduces the palette to a specific size using the Nearest Color algorithm.
    /// </summary>
    /// <param name="max">The new size of the palette</param>
    void ReducePalette(int max)
    {
        while (Palette.Count > max)
        {
            int minidx = -1;
            int newidx = -1;
            long min = long.MaxValue;
            for (int i = 0; i < Palette.Count; i++)
            {
                for (int j = i + 1; j < Palette.Count; j++)
                {
                    Color c1 = Palette[i];
                    Color c2 = Palette[j];
                    long d = c1.Distance(c2);
                    if (d < min)
                    {
                        min = d;
                        minidx = i;
                        newidx = j;
                    }
                }
            }
            if (minidx == -1) throw new PNGException("Error while reducing image palette.");
            List<Color> Keys = new List<Color>();
            foreach (Color key in PaletteReductions.Keys) Keys.Add(key);
            foreach (Color key in Keys)
            {
                if (PaletteReductions[key] == Palette[minidx])
                {
                    // Some other color value was already reduced away and maps into the color that we are now about to reduce again,
                    // so we change that old already-reduced color value to now map to this new value as well.
                    PaletteReductions.Remove(key);
                    PaletteReductions.Add(key, Palette[newidx]);
                }
            }
            // Map the old color value that we're reducing away to the new, closest neighbour color value.
            PaletteReductions.Add(Palette[minidx], Palette[newidx]);
            // Remove the color from the palette.
            Palette.RemoveAt(minidx);
        }
    }

    byte[] EncodePLTE(int BitDepth)
    {
        CreatePalette(BitDepth);
        return BitDepth switch
        {
            0 or 8 => EncodePLTE8(),
            _ => throw new PNGException("Unsupported bit depth."),
        };
    }

    byte[] EncodePLTE8()
    {
        byte[] Bytes = new byte[Height * (Width + 1)];
        for (int y = 0; y < Height; y++)
        {
            byte filter = 0;
            Bytes[y * (Width + 1)] = filter;
            for (int x = 0; x < Width; x++)
            {
                uint srcidx = (uint) (y * Width * 4 + x * 4);
                byte r, g, b, a;
                if (InvertData)
                {
                    r = Raw[srcidx + 3];
                    g = Raw[srcidx + 2];
                    b = Raw[srcidx + 1];
                    a = Raw[srcidx];
                }
                else
                {
                    r = Raw[srcidx];
                    g = Raw[srcidx + 1];
                    b = Raw[srcidx + 2];
                    a = Raw[srcidx + 3];
                }
                Color c = new Color(r, g, b, a);
                int idx = Palette.IndexOf(c);
                if (idx == -1)
                {
                    if (PaletteReductions.ContainsKey(c))
                    {
                        idx = Palette.IndexOf(PaletteReductions[c]);
                        if (idx == -1) throw new PNGException("Could not find a mapping for in palette reduction registry.");
                    }
                    else throw new PNGException($"Could not find a palette entry for color {c}.");
                }
                uint dstidx = (uint) (y * (Width + 1) + 1 + x);
                byte Left = x == 0 ? (byte) 0 : Bytes[dstidx - 1];
                byte Up = y == 0 ? (byte) 0 : Bytes[dstidx - Width - 1];
                byte LeftUp = x == 0 || y == 0 ? (byte) 0 : Raw[dstidx - Width - 2];
                Bytes[dstidx] = Filter((byte) idx, Left, Up, LeftUp, filter);
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

    class PNGPaletteChunk : PNGChunk
    {
        public PNGPaletteChunk(BinaryWriter bw, List<Color> Palette) : base("PLTE", bw)
        {
            foreach (Color c in Palette)
            {
                DataWriter.Write(c.Red);
                DataWriter.Write(c.Green);
                DataWriter.Write(c.Blue);
            }
        }
    }

    class PNGTransparencyChunk : PNGChunk
    {
        public PNGTransparencyChunk(BinaryWriter bw, List<Color> Palette) : base("tRNS", bw)
        {
            foreach (Color c in Palette)
            {
                DataWriter.Write(c.Alpha);
            }
        }
    }
}