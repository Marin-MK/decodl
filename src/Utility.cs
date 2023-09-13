using System;
using System.IO;

namespace decodl;

public static class Utility
{
    public static uint ReadUInt32BE(this BinaryReader BinaryReader)
    {
        uint Result = 0;
        for (int i = 0; i < 4; i++)
        {
            byte Byte = BinaryReader.ReadByte();
            Result += (uint) Byte << (3 - i) * 8;
        }
        return Result;
    }

    public static ushort ReadUInt16BE(this BinaryReader BinaryReader)
    {
        ushort Result = 0;
        for (int i = 0; i < 2; i++)
        {
            byte Byte = BinaryReader.ReadByte();
            Result += (ushort) (Byte << (1 - i) * 8);
        }
        return Result;
    }

    public static int ReadInt32BE(this BinaryReader BinaryReader)
    {
        int Result = 0;
        for (int i = 0; i < 4; i++)
        {
            byte Byte = BinaryReader.ReadByte();
            Result += Byte << (3 - i) * 8;
        }
        return Result;
    }

    public static short ReadInt16BE(this BinaryReader BinaryReader)
    {
        short Result = 0;
        for (int i = 0; i < 2; i++)
        {
            byte Byte = BinaryReader.ReadByte();
            Result += (short) (Byte << (1 - i) * 8);
        }
        return Result;
    }

    public static bool GetBit(this byte Byte, int Index)
    {
        byte Mask = (byte)Math.Pow(2, 7 - Index);
        return (Byte & Mask) == Mask;
    }

    public static byte GetBits(this byte Byte, int Index, int Count)
    {
        if (Index == 0 && Count == 8) return Byte;
        byte Result = 0;
        for (int i = Index; i < Index + Count; i++)
        {
            bool Bit = Byte.GetBit(i);
            if (Bit) Result += (byte)Math.Pow(2, Index + Count - 1 - i);
        }
        return Result;
    }

    public static void SetBits(ref this byte Byte, int Index, int Count, byte Value)
    {
        if (Index == 0 && Count == 8)
        {
            Byte = Value;
            return;
        }
        byte Result = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= Index && i < Index + Count)
            {
                // Part of the value
                if (Value.GetBit(8 - Count + i - Index))
                    Result += (byte)Math.Pow(2, 7 - i);
            }
            else
            {
                // Unchanged
                if (Byte.GetBit(i)) Result += (byte)Math.Pow(2, 7 - i);
            }
        }
        Byte = Result;
    }
}

public enum ColorTypes : byte
{
    Grayscale = 0,
    RGB = 2,
    Indexed = 3,
    GrayscaleAlpha = 4,
    RGBA = 6
}

public enum FilterType : byte
{
    None = 0,
    Sub = 1,
    Up = 2,
    Average = 3,
    Paeth = 4
}

public class PNGException : Exception
{
    public PNGException(string Message) : base(Message) { }
}

public class Color
{
    public byte Red;
    public byte Green;
    public byte Blue;
    public byte Alpha;

    public Color(byte Red, byte Green, byte Blue, byte Alpha = 255)
    {
        this.Red = Red;
        this.Green = Green;
        this.Blue = Blue;
        this.Alpha = Alpha;
    }

    public long Distance(Color c)
    {
        return (this.Red - c.Red) * (this.Red - c.Red) +
               (this.Green - c.Green) * (this.Green - c.Green) +
               (this.Blue - c.Blue) * (this.Blue - c.Blue) +
               (this.Alpha - c.Alpha) * (this.Alpha - c.Alpha);
    }

    public override bool Equals(object obj)
    {
        if (this == obj) return true;
        if (obj is Color)
        {
            Color c = (Color) obj;
            return this.Red == c.Red && this.Green == c.Green && this.Blue == c.Blue && this.Alpha == c.Alpha;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return this.Alpha << 24 + this.Blue << 16 + this.Green << 8 + this.Red;
    }

    public override string ToString()
    {
        return $"({Red}, {Green}, {Blue}, {Alpha})";
    }
}