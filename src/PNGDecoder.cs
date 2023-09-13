using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace decodl;

public class PNGDecoder
{
    protected BinaryReader Stream;

    protected bool SeenPalette = false;
    protected bool SeenData = false;

    public int Width;
    public int Height;
    public byte BitDepth;
    public ColorTypes ColorType;
    public byte Compression;
    public byte Filter;
    public byte Interlacing;

    List<Color> Palette;
    List<byte> AlphaPalette;

    ushort? AlphaGreyValue;
    Color AlphaRGBValue;

    public byte[] Bytes;

    List<PNGChunk> Chunks = new List<PNGChunk>();

    /// <summary>
    /// Decodes the specified PNG file and returns a tuple containing the pixels as RGBA bytes, and the width and height of the image.
    /// </summary>
    public static (byte[] Bytes, int Width, int Height) Decode(string Filename)
    {
        PNGDecoder decoder = new PNGDecoder(Filename);
        return decoder.Decode();
    }

    public PNGDecoder(string Filename)
        : this(new BinaryReader(File.OpenRead(Filename))) { this.Stream.Close(); this.Stream.Dispose(); }
    public PNGDecoder(byte[] Bytes)
        : this(new BinaryReader(new MemoryStream(Bytes, false))) { this.Stream.Close(); this.Stream.Dispose(); }
    public PNGDecoder(BinaryReader Stream)
    {
        this.Stream = Stream;
        TestHeader();
        DecodePNG();
        this.Stream.Close();
    }

    protected void TestHeader()
    {
        byte[] header = { 137, 80, 78, 71, 13, 10, 26, 10 };
        for (int i = 0; i < 8; i++)
        {
            byte b = Stream.ReadByte();
            if (b != header[i]) throw new PNGException($"PNG invalid header sequence.");
        }
    }

    public bool HasChunk(string Header)
    {
        return this.Chunks.Any(c => c.Type == Header);
    }

    public byte[] GetChunk(string Header, bool Decompress)
    {
        PNGChunk chunk = this.Chunks.Find(c => c.Type == Header);
        if (chunk is not PNGUnknownChunk) throw new PNGException("This chunk is already known.");
        PNGUnknownChunk c = (PNGUnknownChunk) chunk;
        byte[] Data = c.Data;
        if (!Decompress) return Data;
        MemoryStream stream = new MemoryStream(Data);
        return DecompressData(stream);
    }

    protected void DecodePNG()
    {
        while (Stream.BaseStream.Position < Stream.BaseStream.Length)
        {
            DecodeChunk();
        }
        ValidatePNG();
    }

    public (byte[] Bytes, int Width, int Height) Decode(bool Parallelism = true)
    {
        return Decode(Environment.ProcessorCount);
    }

    public (byte[] Bytes, int Width, int Height) Decode(int DegreeOfParllelism)
    {
        MemoryStream DataChunks = MergeDataChunks();
        byte[] RawBytes = DecompressData(DataChunks);
        Bytes = new byte[Width * Height * 4];
        if (ColorType == ColorTypes.RGBA)
        {
            if (BitDepth == 8)
            {
                if (DegreeOfParllelism != 0) ConvertRGBA8P(RawBytes, DegreeOfParllelism);
                else ConvertRGBA8(RawBytes);
            }
            else if (BitDepth == 16) ConvertRGBA16(RawBytes);
            else throw new PNGException($"Unhandled Bit Depth {BitDepth}");
        }
        else if (ColorType == ColorTypes.RGB)
        {
            if (BitDepth == 8) ConvertRGB8(RawBytes);
            else if (BitDepth == 16) ConvertRGB16(RawBytes);
            else throw new PNGException($"Unhandled Bit Depth {BitDepth}");
        }
        else if (ColorType == ColorTypes.Grayscale)
        {
            if (BitDepth == 1) ConvertGray1(RawBytes);
            else if (BitDepth == 2) ConvertGray2(RawBytes);
            else if (BitDepth == 4) ConvertGray4(RawBytes);
            else if (BitDepth == 8) ConvertGray8(RawBytes);
            else if (BitDepth == 16) ConvertGray16(RawBytes);
            else throw new PNGException($"Unhandled Bit Depth {BitDepth}");
        }
        else if (ColorType == ColorTypes.GrayscaleAlpha)
        {
            if (BitDepth == 8) ConvertGrayA8(RawBytes);
            else if (BitDepth == 16) ConvertGrayA16(RawBytes);
            else throw new PNGException($"Unhandled Bit Depth {BitDepth}");
        }
        else if (ColorType == ColorTypes.Indexed)
        {
            if (BitDepth == 8) ConvertPLTE8(RawBytes);
            else if (BitDepth == 4) ConvertPLTE4(RawBytes);
            else if (BitDepth == 2) ConvertPLTE2(RawBytes);
            else if (BitDepth == 1) ConvertPLTE1(RawBytes);
            else throw new PNGException($"Unhandled Bit Depth {BitDepth}");
        }
        else throw new PNGException($"Unhandled Color Type {ColorType}");
        return (Bytes, Width, Height);
    }

    protected void ConvertRGBA8(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 4 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 4 + 1) + 1 + x * 4;
                for (int rgba = 0; rgba < 4; rgba++)
                {
                    byte LeftByte = 0;
                    byte UpperByte = 0;
                    byte newvalue = 0;
                    if (filter == 0)
                    {
                        newvalue = RawBytes[realidx + rgba];
                    }
                    else if (filter == 1)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgba];
                        newvalue = (byte)((RawBytes[realidx + rgba] + LeftByte) % 256);
                    }
                    else if (filter == 2)
                    {
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgba];
                        newvalue = (byte)((RawBytes[realidx + rgba] + UpperByte) % 256);
                    }
                    else if (filter == 3)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgba];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgba];
                        newvalue = (byte)((RawBytes[realidx + rgba] + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                    }
                    else if (filter == 4)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgba];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgba];
                        byte UpperLeftByte = 0;
                        if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4 + rgba];

                        int Base = LeftByte + UpperByte - UpperLeftByte;
                        int LeftDiff = Math.Abs(Base - LeftByte);
                        int UpperDiff = Math.Abs(Base - UpperByte);
                        int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                        byte Paeth = 0;
                        if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = LeftByte;
                        else if (UpperDiff <= UpperLeftDiff) Paeth = UpperByte;
                        else Paeth = UpperLeftByte;
                        newvalue = (byte)((RawBytes[realidx + rgba] + Paeth) % 256);
                    }
                    else throw new PNGException($"PNG invalid filter type {filter}.");
                    Bytes[pxidx + rgba] = newvalue;
                }
            }
        }
    }

    protected void ConvertRGBA8P(byte[] RawBytes, int ParDegree)
    {
        List<(int Start, int End, byte[] Result)> Ops = new List<(int, int, byte[])>();
        int lasty = 0;
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 4 + 1)];
            int nextfilter = y + 1 >= Height ? -1 : RawBytes[(y + 1) * (Width * 4 + 1)];
            if (nextfilter == 0 || nextfilter == 1 || nextfilter == -1)
            {
                Ops.Add((lasty, y, new byte[(y - lasty + 1) * Width * 4]));
                lasty = y + 1;
            }
        }

        Parallel.ForEach(Ops, new ParallelOptions() { MaxDegreeOfParallelism = ParDegree }, op =>
        {
            int yoffset = op.Start * Width * 4;
            for (int y = op.Start; y <= op.End; y++)
            {
                int filter = RawBytes[y * (Width * 4 + 1)];
                for (int x = 0; x < Width; x++)
                {
                    int pxidx = y * Width * 4 + x * 4;
                    int realidx = y * (Width * 4 + 1) + 1 + x * 4;
                    for (int rgba = 0; rgba < 4; rgba++)
                    {
                        byte LeftByte = 0;
                        byte UpperByte = 0;
                        byte newvalue = 0;
                        if (filter == 0)
                        {
                            newvalue = RawBytes[realidx + rgba];
                        }
                        else if (filter == 1)
                        {
                            if (x > 0) LeftByte = op.Result[pxidx - 4 + rgba - yoffset];
                            newvalue = (byte)((RawBytes[realidx + rgba] + LeftByte) % 256);
                        }
                        else if (filter == 2)
                        {
                            if (y > 0) UpperByte = op.Result[pxidx - Width * 4 + rgba - yoffset];
                            newvalue = (byte)((RawBytes[realidx + rgba] + UpperByte) % 256);
                        }
                        else if (filter == 3)
                        {
                            if (x > 0) LeftByte = op.Result[pxidx - 4 + rgba - yoffset];
                            if (y > 0) UpperByte = op.Result[pxidx - Width * 4 + rgba - yoffset];
                            newvalue = (byte)((RawBytes[realidx + rgba] + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                        }
                        else if (filter == 4)
                        {
                            if (x > 0) LeftByte = op.Result[pxidx - 4 + rgba - yoffset];
                            if (y > 0) UpperByte = op.Result[pxidx - Width * 4 + rgba - yoffset];
                            byte UpperLeftByte = 0;
                            if (x > 0 && y > 0) UpperLeftByte = op.Result[pxidx - Width * 4 - 4 + rgba - yoffset];

                            int Base = LeftByte + UpperByte - UpperLeftByte;
                            int LeftDiff = Math.Abs(Base - LeftByte);
                            int UpperDiff = Math.Abs(Base - UpperByte);
                            int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                            byte Paeth = 0;
                            if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = LeftByte;
                            else if (UpperDiff <= UpperLeftDiff) Paeth = UpperByte;
                            else Paeth = UpperLeftByte;
                            newvalue = (byte)((RawBytes[realidx + rgba] + Paeth) % 256);
                        }
                        else throw new PNGException($"PNG invalid filter type {filter}.");
                        op.Result[pxidx + rgba - yoffset] = newvalue;
                    }
                }
            }
        });
        Ops.ForEach(op =>
        {
            Array.Copy(op.Result, 0, Bytes, op.Start * Width * 4, op.Result.Length);
        });
    }

    protected void ConvertRGBA16(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 8 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 8 + 1) + 1 + x * 8;
                for (int rgba = 0; rgba < 4; rgba++)
                {
                    short LeftByte = 0;
                    short UpperByte = 0;
                    byte newvalue = 0;
                    if (filter == 0)
                    {
                        newvalue = (byte)((RawBytes[realidx + rgba * 2] * 256 + RawBytes[realidx + rgba * 2 + 1]) / 256);
                    }
                    else if (filter == 1)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgba];
                        newvalue = (byte)(((RawBytes[realidx + rgba * 2] * 256 + RawBytes[realidx + rgba * 2 + 1]) / 256 + LeftByte) % 256);
                    }
                    else if (filter == 2)
                    {
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgba];
                        newvalue = (byte)(((RawBytes[realidx + rgba * 2] * 256 + RawBytes[realidx + rgba * 2 + 1]) / 256 + UpperByte) % 256);
                    }
                    else if (filter == 3)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgba];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgba];
                        newvalue = (byte)(((RawBytes[realidx + rgba * 2] * 256 + RawBytes[realidx + rgba + 1]) / 256 + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                    }
                    else if (filter == 4)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgba];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgba];
                        byte UpperLeftByte = 0;
                        if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4 + rgba];

                        int Base = LeftByte + UpperByte - UpperLeftByte;
                        int LeftDiff = Math.Abs(Base - LeftByte);
                        int UpperDiff = Math.Abs(Base - UpperByte);
                        int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                        byte Paeth = 0;
                        if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = (byte)LeftByte;
                        else if (UpperDiff <= UpperLeftDiff) Paeth = (byte)UpperByte;
                        else Paeth = UpperLeftByte;
                        newvalue = (byte)(((RawBytes[realidx + rgba * 2] * 256 + RawBytes[realidx + rgba * 2 + 1]) / 256 + Paeth) % 256);
                    }
                    else throw new PNGException($"PNG invalid filter type {filter}.");
                    Bytes[pxidx + rgba] = newvalue;
                }
            }
        }
    }

    protected void ConvertRGB8(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 3 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 3 + 1) + 1 + x * 3;
                for (int rgb = 0; rgb < 3; rgb++)
                {
                    byte LeftByte = 0;
                    byte UpperByte = 0;
                    byte newvalue = 0;
                    if (filter == 0)
                    {
                        newvalue = RawBytes[realidx + rgb];
                    }
                    else if (filter == 1)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgb];
                        newvalue = (byte)((RawBytes[realidx + rgb] + LeftByte) % 256);
                    }
                    else if (filter == 2)
                    {
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgb];
                        newvalue = (byte)((RawBytes[realidx + rgb] + UpperByte) % 256);
                    }
                    else if (filter == 3)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgb];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgb];
                        newvalue = (byte)((RawBytes[realidx + rgb] + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                    }
                    else if (filter == 4)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgb];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgb];
                        byte UpperLeftByte = 0;
                        if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4 + rgb];

                        int Base = LeftByte + UpperByte - UpperLeftByte;
                        int LeftDiff = Math.Abs(Base - LeftByte);
                        int UpperDiff = Math.Abs(Base - UpperByte);
                        int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                        byte Paeth = 0;
                        if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = LeftByte;
                        else if (UpperDiff <= UpperLeftDiff) Paeth = UpperByte;
                        else Paeth = UpperLeftByte;
                        newvalue = (byte)((RawBytes[realidx + rgb] + Paeth) % 256);
                    }
                    else throw new PNGException($"PNG invalid filter type {filter}.");
                    Bytes[pxidx + rgb] = newvalue;
                }
                Bytes[pxidx + 3] = (byte)(AlphaRGBValue != null && Bytes[pxidx] == AlphaRGBValue.Red && Bytes[pxidx + 1] == AlphaRGBValue.Green && Bytes[pxidx + 2] == AlphaRGBValue.Blue ? 0 : 255);
            }
        }
    }

    protected void ConvertRGB16(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 6 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 6 + 1) + 1 + x * 6;
                for (int rgb = 0; rgb < 3; rgb++)
                {
                    short LeftByte = 0;
                    short UpperByte = 0;
                    byte newvalue = 0;
                    if (filter == 0)
                    {
                        newvalue = (byte)((RawBytes[realidx + rgb * 2] * 256 + RawBytes[realidx + rgb * 2 + 1]) / 256);
                    }
                    else if (filter == 1)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgb];
                        newvalue = (byte)(((RawBytes[realidx + rgb * 2] * 256 + RawBytes[realidx + rgb * 2 + 1]) / 256 + LeftByte) % 256);
                    }
                    else if (filter == 2)
                    {
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgb];
                        newvalue = (byte)(((RawBytes[realidx + rgb * 2] * 256 + RawBytes[realidx + rgb * 2 + 1]) / 256 + UpperByte) % 256);
                    }
                    else if (filter == 3)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgb];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgb];
                        newvalue = (byte)(((RawBytes[realidx + rgb * 2] * 256 + RawBytes[realidx + rgb + 1]) / 256 + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                    }
                    else if (filter == 4)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + rgb];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + rgb];
                        byte UpperLeftByte = 0;
                        if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4 + rgb];

                        int Base = LeftByte + UpperByte - UpperLeftByte;
                        int LeftDiff = Math.Abs(Base - LeftByte);
                        int UpperDiff = Math.Abs(Base - UpperByte);
                        int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                        byte Paeth = 0;
                        if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = (byte)LeftByte;
                        else if (UpperDiff <= UpperLeftDiff) Paeth = (byte)UpperByte;
                        else Paeth = UpperLeftByte;
                        newvalue = (byte)(((RawBytes[realidx + rgb * 2] * 256 + RawBytes[realidx + rgb * 2 + 1]) / 256 + Paeth) % 256);
                    }
                    else throw new PNGException($"PNG invalid filter type {filter}.");
                    Bytes[pxidx + rgb] = newvalue;
                }
                Bytes[pxidx + 3] = (byte)(AlphaRGBValue != null && Bytes[pxidx] == AlphaRGBValue.Red && Bytes[pxidx + 1] == AlphaRGBValue.Green && Bytes[pxidx + 2] == AlphaRGBValue.Blue ? 0 : 255);
            }
        }
    }

    protected void ConvertGray8(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width + 1) + 1 + x;
                byte LeftByte = 0;
                byte UpperByte = 0;
                byte newvalue = 0;
                if (filter == 0)
                {
                    newvalue = RawBytes[realidx];
                }
                else if (filter == 1)
                {
                    if (x > 0) LeftByte = Bytes[pxidx - 4];
                    newvalue = (byte)((RawBytes[realidx] + LeftByte) % 256);
                }
                else if (filter == 2)
                {
                    if (y > 0) UpperByte = Bytes[pxidx - Width * 4];
                    newvalue = (byte)((RawBytes[realidx] + UpperByte) % 256);
                }
                else if (filter == 3)
                {
                    if (x > 0) LeftByte = Bytes[pxidx - 4];
                    if (y > 0) UpperByte = Bytes[pxidx - Width * 4];
                    newvalue = (byte)((RawBytes[realidx] + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                }
                else if (filter == 4)
                {
                    if (x > 0) LeftByte = Bytes[pxidx - 4];
                    if (y > 0) UpperByte = Bytes[pxidx - Width * 4];
                    byte UpperLeftByte = 0;
                    if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4];

                    int Base = LeftByte + UpperByte - UpperLeftByte;
                    int LeftDiff = Math.Abs(Base - LeftByte);
                    int UpperDiff = Math.Abs(Base - UpperByte);
                    int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                    byte Paeth = 0;
                    if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = LeftByte;
                    else if (UpperDiff <= UpperLeftDiff) Paeth = UpperByte;
                    else Paeth = UpperLeftByte;
                    newvalue = (byte)((RawBytes[realidx] + Paeth) % 256);
                }
                else throw new PNGException($"PNG invalid filter type {filter}.");
                Bytes[pxidx] = newvalue;
                Bytes[pxidx + 1] = newvalue;
                Bytes[pxidx + 2] = newvalue;
                Bytes[pxidx + 3] = (byte)(newvalue == AlphaGreyValue ? 0 : 255);
            }
        }
    }

    protected void ConvertGray4(byte[] RawBytes)
    {
        FilterBytes(2, RawBytes);
        int scanlinewidth = Width / 2 + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x / 2;
                byte mask = (byte)(x % 2 == 0 ? 0xF0 : 0x0F);
                byte shift = (byte)(x % 2 == 0 ? 4 : 0);
                byte value = (byte)((RawBytes[realidx] & mask) >> shift);
                byte gvalue = (byte)Math.Round(value / 15d * 255d);
                Bytes[pxidx] = gvalue;
                Bytes[pxidx + 1] = gvalue;
                Bytes[pxidx + 2] = gvalue;
                Bytes[pxidx + 3] = (byte)(gvalue == AlphaGreyValue ? 0 : 255);
            }
        }
    }

    protected void ConvertGray2(byte[] RawBytes)
    {
        FilterBytes(4, RawBytes);
        int scanlinewidth = Width / 4 + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x / 4;
                byte mask = (byte)(x % 2 == 0 ? 0xF0 : 0x0F);
                byte shift = (byte)(x % 2 == 0 ? 4 : 0);
                byte value = (byte)((RawBytes[realidx] & mask) >> shift);
                byte gvalue = (byte)Math.Round(value / 3d * 255d);
                Bytes[pxidx] = gvalue;
                Bytes[pxidx + 1] = gvalue;
                Bytes[pxidx + 2] = gvalue;
                Bytes[pxidx + 3] = (byte)(gvalue == AlphaGreyValue ? 0 : 255);
            }
        }
    }

    protected void ConvertGray1(byte[] RawBytes)
    {
        FilterBytes(8, RawBytes);
        int scanlinewidth = Width / 8 + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x / 8;
                byte xoff = (byte)(x % 8);
                byte mask = (byte)(1 << (7 - xoff));
                byte shift = (byte)(7 - xoff);
                byte value = (byte)((RawBytes[realidx] & mask) >> shift);
                byte gvalue = (byte) (value == 0 ? 0 : 255);
                Bytes[pxidx] = gvalue;
                Bytes[pxidx + 1] = gvalue;
                Bytes[pxidx + 2] = gvalue;
                Bytes[pxidx + 3] = (byte)(gvalue == AlphaGreyValue ? 0 : 255);
            }
        }
    }

    protected void ConvertGray16(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 2 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 2 + 1) + 1 + x * 2;
                short LeftByte = 0;
                short UpperByte = 0;
                byte newvalue = 0;
                if (filter == 0)
                {
                    newvalue = (byte)((RawBytes[realidx] * 256 + RawBytes[realidx + 1]) / 256);
                }
                else if (filter == 1)
                {
                    if (x > 0) LeftByte = Bytes[pxidx - 4];
                    newvalue = (byte)(((RawBytes[realidx] * 256 + RawBytes[realidx + 1]) / 256 + LeftByte) % 256);
                }
                else if (filter == 2)
                {
                    if (y > 0) UpperByte = Bytes[pxidx - Width * 4];
                    newvalue = (byte)(((RawBytes[realidx] * 256 + RawBytes[realidx + 1]) / 256 + UpperByte) % 256);
                }
                else if (filter == 3)
                {
                    if (x > 0) LeftByte = Bytes[pxidx - 4];
                    if (y > 0) UpperByte = Bytes[pxidx - Width * 4];
                    newvalue = (byte)(((RawBytes[realidx] * 256 + RawBytes[realidx + 1]) / 256 + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                }
                else if (filter == 4)
                {
                    if (x > 0) LeftByte = Bytes[pxidx - 4];
                    if (y > 0) UpperByte = Bytes[pxidx - Width * 4];
                    byte UpperLeftByte = 0;
                    if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4];

                    int Base = LeftByte + UpperByte - UpperLeftByte;
                    int LeftDiff = Math.Abs(Base - LeftByte);
                    int UpperDiff = Math.Abs(Base - UpperByte);
                    int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                    byte Paeth = 0;
                    if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = (byte)LeftByte;
                    else if (UpperDiff <= UpperLeftDiff) Paeth = (byte)UpperByte;
                    else Paeth = UpperLeftByte;
                    newvalue = (byte)(((RawBytes[realidx] * 256 + RawBytes[realidx + 1]) / 256 + Paeth) % 256);
                }
                else throw new PNGException($"PNG invalid filter type {filter}.");
                Bytes[pxidx] = newvalue;
                Bytes[pxidx + 1] = newvalue;
                Bytes[pxidx + 2] = newvalue;
                Bytes[pxidx + 3] = 255;
            }
        }
    }

    protected void ConvertGrayA8(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 2 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 2 + 1) + 1 + x * 2;
                for (int ga = 0; ga < 2; ga++)
                {
                    byte LeftByte = 0;
                    byte UpperByte = 0;
                    byte newvalue = 0;
                    if (filter == 0)
                    {
                        newvalue = RawBytes[realidx + ga];
                    }
                    else if (filter == 1)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + (ga == 0 ? 0 : 3)];
                        newvalue = (byte)((RawBytes[realidx + ga] + LeftByte) % 256);
                    }
                    else if (filter == 2)
                    {
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + (ga == 0 ? 0 : 3)];
                        newvalue = (byte)((RawBytes[realidx + ga] + UpperByte) % 256);
                    }
                    else if (filter == 3)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + (ga == 0 ? 0 : 3)];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + (ga == 0 ? 0 : 3)];
                        newvalue = (byte)((RawBytes[realidx + ga] + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                    }
                    else if (filter == 4)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + (ga == 0 ? 0 : 3)];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + (ga == 0 ? 0 : 3)];
                        byte UpperLeftByte = 0;
                        if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4 + (ga == 0 ? 0 : 3)];

                        int Base = LeftByte + UpperByte - UpperLeftByte;
                        int LeftDiff = Math.Abs(Base - LeftByte);
                        int UpperDiff = Math.Abs(Base - UpperByte);
                        int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                        byte Paeth = 0;
                        if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = LeftByte;
                        else if (UpperDiff <= UpperLeftDiff) Paeth = UpperByte;
                        else Paeth = UpperLeftByte;
                        newvalue = (byte)((RawBytes[realidx + ga] + Paeth) % 256);
                    }
                    else throw new PNGException($"PNG invalid filter type {filter}.");
                    if (ga == 0)
                    {
                        Bytes[pxidx] = newvalue;
                        Bytes[pxidx + 1] = newvalue;
                        Bytes[pxidx + 2] = newvalue;
                    }
                    else
                    {
                        Bytes[pxidx + 3] = newvalue;
                    }
                }
            }
        }
    }

    protected void ConvertGrayA16(byte[] RawBytes)
    {
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * (Width * 4 + 1)];
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * (Width * 4 + 1) + 1 + x * 4;
                for (int ga = 0; ga < 2; ga++)
                {
                    short LeftByte = 0;
                    short UpperByte = 0;
                    byte newvalue = 0;
                    if (filter == 0)
                    {
                        newvalue = (byte)((RawBytes[realidx + ga * 2] * 256 + RawBytes[realidx + ga * 2 + 1]) / 256);
                    }
                    else if (filter == 1)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + (ga == 0 ? 0 : 3)];
                        newvalue = (byte)(((RawBytes[realidx + ga * 2] * 256 + RawBytes[realidx + ga * 2 + 1]) / 256 + LeftByte) % 256);
                    }
                    else if (filter == 2)
                    {
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + (ga == 0 ? 0 : 3)];
                        newvalue = (byte)(((RawBytes[realidx + ga * 2] * 256 + RawBytes[realidx + ga * 2 + 1]) / 256 + UpperByte) % 256);
                    }
                    else if (filter == 3)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + (ga == 0 ? 0 : 3)];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + (ga == 0 ? 0 : 3)];
                        newvalue = (byte)(((RawBytes[realidx + ga * 2] * 256 + RawBytes[realidx + ga * 2 + 1]) / 256 + (byte)Math.Floor((LeftByte + UpperByte) / 2d)) % 256);
                    }
                    else if (filter == 4)
                    {
                        if (x > 0) LeftByte = Bytes[pxidx - 4 + (ga == 0 ? 0 : 3)];
                        if (y > 0) UpperByte = Bytes[pxidx - Width * 4 + (ga == 0 ? 0 : 3)];
                        byte UpperLeftByte = 0;
                        if (x > 0 && y > 0) UpperLeftByte = Bytes[pxidx - Width * 4 - 4 + (ga == 0 ? 0 : 3)];

                        int Base = LeftByte + UpperByte - UpperLeftByte;
                        int LeftDiff = Math.Abs(Base - LeftByte);
                        int UpperDiff = Math.Abs(Base - UpperByte);
                        int UpperLeftDiff = Math.Abs(Base - UpperLeftByte);

                        byte Paeth = 0;
                        if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = (byte)LeftByte;
                        else if (UpperDiff <= UpperLeftDiff) Paeth = (byte)UpperByte;
                        else Paeth = UpperLeftByte;
                        newvalue = (byte)(((RawBytes[realidx + ga * 2] * 256 + RawBytes[realidx + ga * 2 + 1]) / 256 + Paeth) % 256);
                    }
                    else throw new PNGException($"PNG invalid filter type {filter}.");
                    if (ga == 0)
                    {
                        Bytes[pxidx] = newvalue;
                        Bytes[pxidx + 1] = newvalue;
                        Bytes[pxidx + 2] = newvalue;
                    }
                    else
                    {
                        Bytes[pxidx + 3] = newvalue;
                    }
                }
            }
        }
    }

    protected void ConvertPLTE8(byte[] RawBytes)
    {
        FilterBytes(1, RawBytes);
        int scanlinewidth = Width + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x;
                byte value = RawBytes[realidx];
                Bytes[pxidx] = Palette[value].Red;
                Bytes[pxidx + 1] = Palette[value].Green;
                Bytes[pxidx + 2] = Palette[value].Blue;
                Bytes[pxidx + 3] = AlphaPalette == null || value >= AlphaPalette.Count ? (byte)255 : AlphaPalette[value];
            }
        }
    }

    protected void ConvertPLTE4(byte[] RawBytes)
    {
        FilterBytes(2, RawBytes);
        int scanlinewidth = Width / 2 + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x / 2;
                byte mask = (byte)(x % 2 == 0 ? 0xF0 : 0x0F);
                byte shift = (byte)(x % 2 == 0 ? 4 : 0);
                byte value = (byte)((RawBytes[realidx] & mask) >> shift);
                Bytes[pxidx] = Palette[value].Red;
                Bytes[pxidx + 1] = Palette[value].Green;
                Bytes[pxidx + 2] = Palette[value].Blue;
                Bytes[pxidx + 3] = AlphaPalette == null || value >= AlphaPalette.Count ? (byte) 255 : AlphaPalette[value];
            }
        }
    }

    protected void ConvertPLTE2(byte[] RawBytes)
    {
        FilterBytes(4, RawBytes);
        int scanlinewidth = Width / 4 + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x / 4;
                byte xoff = (byte)(x % 4);
                byte mask = (byte)(xoff == 0 ? 0xFF : xoff == 1 ? 0x3F : xoff == 2 ? 0x0F : 0x03);
                byte shift = (byte)(xoff == 0 ? 6 : xoff == 1 ? 4 : xoff == 2 ? 2 : 0);
                byte value = (byte)((RawBytes[realidx] & mask) >> shift);
                Bytes[pxidx] = Palette[value].Red;
                Bytes[pxidx + 1] = Palette[value].Green;
                Bytes[pxidx + 2] = Palette[value].Blue;
                Bytes[pxidx + 3] = AlphaPalette == null || value >= AlphaPalette.Count ? (byte)255 : AlphaPalette[value];
            }
        }
    }

    protected void ConvertPLTE1(byte[] RawBytes)
    {
        FilterBytes(8, RawBytes);
        int scanlinewidth = Width / 8 + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pxidx = y * Width * 4 + x * 4;
                int realidx = y * scanlinewidth + 1 + x / 8;
                byte xoff = (byte)(x % 8);
                byte mask = (byte)(1 << (7 - xoff));
                byte shift = (byte)(7 - xoff);
                byte value = (byte)((RawBytes[realidx] & mask) >> shift);
                Bytes[pxidx] = Palette[value].Red;
                Bytes[pxidx + 1] = Palette[value].Green;
                Bytes[pxidx + 2] = Palette[value].Blue;
                Bytes[pxidx + 3] = AlphaPalette == null || value >= AlphaPalette.Count ? (byte)255 : AlphaPalette[value];
            }
        }
    }

    protected void FilterBytes(int SamplesPerByte, byte[] RawBytes)
    {
        int scanlinewidth = Width / SamplesPerByte + 1;
        for (int y = 0; y < Height; y++)
        {
            int filter = RawBytes[y * scanlinewidth];
            for (int x = 0; x < Width / SamplesPerByte; x++)
            {
                int idx = y * scanlinewidth + x + 1;
                byte left = x > 0 ? RawBytes[idx - 1] : (byte) 0;
                byte up = y > 0 ? RawBytes[idx - scanlinewidth] : (byte) 0;
                byte upleft = x > 0 && y > 0 ? RawBytes[idx - scanlinewidth - 1] : (byte) 0;
                byte value = RawBytes[idx];
                byte newvalue = 0;
                if (filter == 0)
                {
                    newvalue = value;
                }
                else if (filter == 1)
                {
                    newvalue = (byte)((value + left) % 256);
                }
                else if (filter == 2)
                {
                    newvalue = (byte)((value + up) % 256);
                }
                else if (filter == 3)
                {
                    newvalue = (byte)((value + (byte)Math.Floor((left + up) / 2d)) % 256);
                }
                else if (filter == 4)
                {
                    int Base = left + up - upleft;
                    int LeftDiff = Math.Abs(Base - left);
                    int UpperDiff = Math.Abs(Base - up);
                    int UpperLeftDiff = Math.Abs(Base - upleft);

                    byte Paeth = 0;
                    if (LeftDiff <= UpperDiff && LeftDiff <= UpperLeftDiff) Paeth = left;
                    else if (UpperDiff <= UpperLeftDiff) Paeth = up;
                    else Paeth = upleft;
                    newvalue = (byte)((value + Paeth) % 256);
                }
                else throw new PNGException($"PNG invalid filter type {filter}.");
                RawBytes[idx] = newvalue;
            }
        }
    }

    protected void ValidatePNG()
    {
        if (ColorType == ColorTypes.Indexed && !SeenPalette)
        {
            throw new PNGException("PNG requires a PLTE palette chunk with color type 3, but no such chunk was found.");
        }
        if (!SeenData)
        {
            throw new PNGException("PNG did not contain an IDATA chunk.");
        }
    }

    protected MemoryStream MergeDataChunks()
    {
        MemoryStream stream = new MemoryStream();
        for (int i = 1; i < Chunks.Count; i++)
        {
            if (Chunks[i].Type == "IDAT")
            {
                byte[] bytedata = ((PNGDataChunk)Chunks[i]).ByteData;
                stream.Write(bytedata, 0, bytedata.Length);
            }
        }
        stream.Flush();
        return stream;
    }

    protected byte[] DecompressData(MemoryStream stream)
    {
        stream.Seek(2, SeekOrigin.Begin);
        MemoryStream output = new MemoryStream();
        using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Decompress))
        {
            deflate.CopyTo(output);
            deflate.Close();
        }
        byte[] data = output.ToArray();
        output.Dispose();
        stream.Dispose();
        return data;
    }

    protected PNGChunk CreateChunk(int ChunkNumber, string Type)
    {
        if (ChunkNumber == 1 && Type != "IHDR") throw new PNGException($"PNG first chunk must be of type IHDR, but got {Type} intead.");
        //if (Type != "IDAT" && Type != "IEND" && Type != "tIME" && Type != "iTXt" && Type != "tEXt" && Type != "zTXt" && SeenData)
        //    throw new PNGException($"PNG data chunks cannot be interrupted by a chunk of type {Type}.");
        switch (Type)
        {
            case "IHDR":
                return new PNGHeaderChunk();
            case "PLTE":
                if (SeenData) throw new PNGException($"PNG PLTE palette chunk must precede the first IDAT chunk.");
                if (SeenPalette) throw new PNGException($"PNG can only contain one PLTE palette chunk, but more were found.");
                if (ColorType == ColorTypes.Grayscale || ColorType == ColorTypes.GrayscaleAlpha)
                {
                    throw new PNGException($"PNG prohibits the presence of a PLTE palette chunk for color type {(int)ColorType}.");
                }
                SeenPalette = true;
                return new PNGPaletteChunk();
            case "tRNS":
                return new PNGTransparencyChunk();
            case "IDAT":
                SeenData = true;
                return new PNGDataChunk();
            case "IEND":
                return new PNGEndChunk();
            default:
                return new PNGUnknownChunk();
        }
    }
    
    protected void DecodeChunk()
    {
        uint Length = Utility.ReadUInt32BE(Stream);
        string Type = "";
        for (int i = 0; i < 4; i++)
        {
            Type += (char) Stream.ReadByte();
        }
        PNGChunk Chunk = CreateChunk(this.Chunks.Count + 1, Type);
        Chunk.Decoder = this;
        Chunk.Length = Length;
        Chunk.Type = Type;
        Chunk.Stream = Stream;
        Chunk.PreParse();
        Chunk.Parse();
        Chunk.PostParse();
        Chunk.Validate();
        this.Chunks.Add(Chunk);
    }

    protected class PNGChunk
    {
        public PNGDecoder Decoder;
        public BinaryReader Stream;

        public uint Length;
        public string Type;
        public uint CRC;

        protected long StartPos;

        public virtual void PreParse()
        {
            StartPos = Stream.BaseStream.Position;
        }

        public virtual void Parse()
        {
            Type t = GetType();
            if (t == typeof(PNGChunk)) // Unknown chunk type
            {
                // Skip unused bytes
                Stream.BaseStream.Position += Length;
            }
        }

        public virtual void Validate()
        {

        }

        public virtual void PostParse()
        {
            if (Stream.BaseStream.Position - StartPos < Length || Stream.BaseStream.Position - StartPos > Length)
            {
                throw new PNGException($"PNG Chunk has a length of {Length} bytes, but {Stream.BaseStream.Position - StartPos} were read.");
            }
            else
            {
                this.CRC = Stream.ReadUInt32BE();
            }
        }

        protected void GuardLength(int Length)
        {
            if (this.Length != Length) throw new PNGException($"PNG Header chunk expected to have size of {Length}, but got {this.Length}.");
        }
    }

    protected class PNGHeaderChunk : PNGChunk
    {
        public int Width;
        public int Height;
        public byte BitDepth;
        public byte ColorType;
        public byte Compression;
        public byte Filter;
        public byte Interlacing;

        public override void Parse()
        {
            GuardLength(13);
            this.Width = Stream.ReadInt32BE();
            this.Height = Stream.ReadInt32BE();
            this.BitDepth = Stream.ReadByte();
            this.ColorType = Stream.ReadByte();
            this.Compression = Stream.ReadByte();
            this.Filter = Stream.ReadByte();
            this.Interlacing = Stream.ReadByte();
            base.Parse();
        }

        public override void Validate()
        {
            if (Width == 0) throw new PNGException($"PNG width must 1 or greater, but got {Width}.");
            if (Height == 0) throw new PNGException($"PNG height must 1 or greater, but got {Height}.");
            if (ColorType == 0)
            {
                if (BitDepth != 1 && BitDepth != 2 && BitDepth != 4 && BitDepth != 8 && BitDepth != 16)
                    throw new PNGException($"PNG bit depth must be either 1, 2, 4, 8 or 16 with a color type of 0.");
            }
            else if (ColorType == 0 || ColorType == 2 || ColorType == 4 || ColorType == 6)
            {
                if (BitDepth != 8 && BitDepth != 16)
                    throw new PNGException($"PNG bit depth must be either 8 or 16 with a color type of {ColorType}.");
            }
            else if (ColorType == 3)
            {
                if (BitDepth != 1 && BitDepth != 2 && BitDepth != 4 && BitDepth != 8)
                    throw new PNGException($"PNG bit depth must be either 1, 2, 4 or 8 with a color type of 3.");
            }
            else
            {
                throw new PNGException($"PNG unknown color type {ColorType}.");
            }
            Decoder.Width = Width;
            Decoder.Height = Height;
            Decoder.BitDepth = BitDepth;
            Decoder.ColorType = (ColorTypes)ColorType;
            Decoder.Compression = Compression;
            Decoder.Filter = Filter;
            Decoder.Interlacing = Interlacing;
        }
    }

    protected class PNGUnknownChunk : PNGChunk
    {
        public byte[] Data;

        public override void Parse()
        {
            Data = Stream.ReadBytes((int) Length);
            base.Parse();
        }
    }

    protected class PNGPaletteChunk : PNGChunk
    {
        public override void Parse()
        {
            Decoder.Palette = new List<Color>();
            if (Length == 0) throw new PNGException($"PNG PLTE palette chunk cannot be empty.");
            if (Length % 3 != 0) throw new PNGException($"PNG PLTE palette chunk length must be divisble by 3.");
            if (Length > 256 * 3) throw new PNGException($"PNG PLTE palette chunk can only have 255 palette entries, but has {Length / 3}.");
            for (int i = 0; i < Length / 3; i++)
            {
                Decoder.Palette.Add(new Color(Stream.ReadByte(), Stream.ReadByte(), Stream.ReadByte()));
            }
            base.Parse();
        }
    }

    protected class PNGTransparencyChunk : PNGChunk
    {
        public override void Parse()
        {
            if (Decoder.SeenPalette && Decoder.ColorType == ColorTypes.Indexed || Decoder.ColorType != ColorTypes.Indexed)
            {
                if (Decoder.ColorType == ColorTypes.Grayscale)
                {
                    Decoder.AlphaGreyValue = Stream.ReadUInt16BE();
                }
                else if (Decoder.ColorType == ColorTypes.RGB)
                {
                    Decoder.AlphaRGBValue = new Color((byte)Stream.ReadUInt16BE(), (byte)Stream.ReadUInt16BE(), (byte)Stream.ReadUInt16BE());
                }
                else if (Decoder.ColorType == ColorTypes.Indexed)
                {
                    Decoder.AlphaPalette = new List<byte>();
                    for (int i = 0; i < Length; i++)
                    {
                        Decoder.AlphaPalette.Add(Stream.ReadByte());
                    }
                }
                else
                {
                    throw new PNGException($"PNG cannot contain tRNS chunk with color type {(int)Decoder.ColorType}");
                }
            }
            else if (Decoder.ColorType == ColorTypes.Indexed)
            {
                throw new PNGException("PNG cannot parse tRNS chunk when no PLTE chunk is present");
            }
            base.Parse();
        }
    }

    protected class PNGDataChunk : PNGChunk
    {
        public byte[] ByteData;

        public override void Parse()
        {
            ByteData = Stream.ReadBytes((int)Length);
            base.Parse();
        }
    }

    protected class PNGEndChunk : PNGChunk { }
}