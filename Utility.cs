using System;
using System.IO;

namespace decodl
{
    public static class Utility
    {
        public static uint ReadUInt32BE(this BinaryReader BinaryReader)
        {
            uint Result = 0;
            for (int i = 0; i < 4; i++)
            {
                byte Byte = BinaryReader.ReadByte();
                Result = Result + Byte << (3 - i) * 8;
            }
            return Result;
        }

        public static ushort ReadUInt16BE(this BinaryReader BinaryReader)
        {
            ushort Result = 0;
            for (int i = 0; i < 2; i++)
            {
                byte Byte = BinaryReader.ReadByte();
                Result = (ushort) (Result + (Byte << (1 - i) * 8));
            }
            return Result;
        }

        public static int ReadInt32BE(this BinaryReader BinaryReader)
        {
            int Result = 0;
            for (int i = 0; i < 4; i++)
            {
                byte Byte = BinaryReader.ReadByte();
                Result = Result + Byte << (3 - i) * 8;
            }
            return Result;
        }

        public static short ReadInt16BE(this BinaryReader BinaryReader)
        {
            short Result = 0;
            for (int i = 0; i < 2; i++)
            {
                byte Byte = BinaryReader.ReadByte();
                Result = (short) (Result + (Byte << (1 - i) * 8));
            }
            return Result;
        }

        public static bool GetBit(this byte Byte, int Index)
        {
            byte Mask = (byte) Math.Pow(2, 7 - Index);
            return (Byte & Mask) == Mask;
        }

        public static byte GetBits(this byte Byte, int Index, int Count)
        {
            if (Index == 0 && Count == 8) return Byte;
            byte Result = 0;
            for (int i = Index; i < Index + Count; i++)
            {
                bool Bit = Byte.GetBit(i);
                if (Bit) Result += (byte) Math.Pow(2, Index + Count - 1 - i);
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
                        Result += (byte) Math.Pow(2, 7 - i);
                }
                else
                {
                    // Unchanged
                    if (Byte.GetBit(i)) Result += (byte) Math.Pow(2, 7 - i);
                }
            }
            Byte = Result;
        }
    }
}
