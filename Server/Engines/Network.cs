using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Accounting;
using Server.ContextMenus;
using Server.Diagnostics;
using Server.Gumps;
using Server.HuePickers;
using Server.Items;
using Server.Menus;
using Server.Menus.ItemLists;
using Server.Menus.Questions;
using Server.Mobiles;
using Server.Movement;
using Server.Network;
using Server.Prompts;
using Server.Targeting;

using CV = Server.ClientVersion;

namespace Server.Network
{
    public class BufferPool
    {
        private static List<BufferPool> m_Pools = new List<BufferPool>();

        public static List<BufferPool> Pools { get { return m_Pools; } set { m_Pools = value; } }

        private string m_Name;

        private int m_InitialCapacity;
        private int m_BufferSize;

        private int m_Misses;

        private Queue<byte[]> m_FreeBuffers;

        public void GetInfo(out string name, out int freeCount, out int initialCapacity, out int currentCapacity, out int bufferSize, out int misses)
        {
            lock (this)
            {
                name = m_Name;
                freeCount = m_FreeBuffers.Count;
                initialCapacity = m_InitialCapacity;
                currentCapacity = m_InitialCapacity * (1 + m_Misses);
                bufferSize = m_BufferSize;
                misses = m_Misses;
            }
        }

        public BufferPool(string name, int initialCapacity, int bufferSize)
        {
            m_Name = name;

            m_InitialCapacity = initialCapacity;
            m_BufferSize = bufferSize;

            m_FreeBuffers = new Queue<byte[]>(initialCapacity);

            for (int i = 0; i < initialCapacity; ++i)
                m_FreeBuffers.Enqueue(new byte[bufferSize]);

            lock (m_Pools)
                m_Pools.Add(this);
        }

        public byte[] AcquireBuffer()
        {
            lock (this)
            {
                if (m_FreeBuffers.Count > 0)
                    return m_FreeBuffers.Dequeue();

                ++m_Misses;

                for (int i = 0; i < m_InitialCapacity; ++i)
                    m_FreeBuffers.Enqueue(new byte[m_BufferSize]);

                return m_FreeBuffers.Dequeue();
            }
        }

        public void ReleaseBuffer(byte[] buffer)
        {
            if (buffer == null)
                return;

            lock (this)
                m_FreeBuffers.Enqueue(buffer);
        }

        public void Free()
        {
            lock (m_Pools)
                m_Pools.Remove(this);
        }
    }

    public class ByteQueue
    {
        private int m_Head;
        private int m_Tail;
        private int m_Size;

        private byte[] m_Buffer;

        public int Length { get { return m_Size; } }

        public ByteQueue()
        {
            m_Buffer = new byte[2048];
        }

        public void Clear()
        {
            m_Head = 0;
            m_Tail = 0;
            m_Size = 0;
        }

        private void SetCapacity(int capacity)
        {
            byte[] newBuffer = new byte[capacity];

            if (m_Size > 0)
            {
                if (m_Head < m_Tail)
                {
                    Buffer.BlockCopy(m_Buffer, m_Head, newBuffer, 0, m_Size);
                }
                else
                {
                    Buffer.BlockCopy(m_Buffer, m_Head, newBuffer, 0, m_Buffer.Length - m_Head);
                    Buffer.BlockCopy(m_Buffer, 0, newBuffer, m_Buffer.Length - m_Head, m_Tail);
                }
            }

            m_Head = 0;
            m_Tail = m_Size;
            m_Buffer = newBuffer;
        }

        public byte GetPacketID()
        {
            if (m_Size >= 1)
                return m_Buffer[m_Head];

            return 0xFF;
        }

        public int GetPacketLength()
        {
            if (m_Size >= 3)
                return (m_Buffer[(m_Head + 1) % m_Buffer.Length] << 8) | m_Buffer[(m_Head + 2) % m_Buffer.Length];

            return 0;
        }

        public int Dequeue(byte[] buffer, int offset, int size)
        {
            if (size > m_Size)
                size = m_Size;

            if (size == 0)
                return 0;

            if (m_Head < m_Tail)
            {
                Buffer.BlockCopy(m_Buffer, m_Head, buffer, offset, size);
            }
            else
            {
                int rightLength = (m_Buffer.Length - m_Head);

                if (rightLength >= size)
                {
                    Buffer.BlockCopy(m_Buffer, m_Head, buffer, offset, size);
                }
                else
                {
                    Buffer.BlockCopy(m_Buffer, m_Head, buffer, offset, rightLength);
                    Buffer.BlockCopy(m_Buffer, 0, buffer, offset + rightLength, size - rightLength);
                }
            }

            m_Head = (m_Head + size) % m_Buffer.Length;
            m_Size -= size;

            if (m_Size == 0)
            {
                m_Head = 0;
                m_Tail = 0;
            }

            return size;
        }

        public void Enqueue(byte[] buffer, int offset, int size)
        {
            if ((m_Size + size) > m_Buffer.Length)
                SetCapacity((m_Size + size + 2047) & ~2047);

            if (m_Head < m_Tail)
            {
                int rightLength = (m_Buffer.Length - m_Tail);

                if (rightLength >= size)
                {
                    Buffer.BlockCopy(buffer, offset, m_Buffer, m_Tail, size);
                }
                else
                {
                    Buffer.BlockCopy(buffer, offset, m_Buffer, m_Tail, rightLength);
                    Buffer.BlockCopy(buffer, offset + rightLength, m_Buffer, 0, size - rightLength);
                }
            }
            else
            {
                Buffer.BlockCopy(buffer, offset, m_Buffer, m_Tail, size);
            }

            m_Tail = (m_Tail + size) % m_Buffer.Length;
            m_Size += size;
        }
    }

    /// <summary>
    /// Handles outgoing packet compression for the network.
    /// </summary>
    public static class Compression
    {
        private static int[] _huffmanTable = new int[514]
        {
            0x2, 0x000, 0x5, 0x01F, 0x6, 0x022, 0x7, 0x034, 0x7, 0x075, 0x6, 0x028, 0x6, 0x03B, 0x7, 0x032,
            0x8, 0x0E0, 0x8, 0x062, 0x7, 0x056, 0x8, 0x079, 0x9, 0x19D, 0x8, 0x097, 0x6, 0x02A, 0x7, 0x057,
            0x8, 0x071, 0x8, 0x05B, 0x9, 0x1CC, 0x8, 0x0A7, 0x7, 0x025, 0x7, 0x04F, 0x8, 0x066, 0x8, 0x07D,
            0x9, 0x191, 0x9, 0x1CE, 0x7, 0x03F, 0x9, 0x090, 0x8, 0x059, 0x8, 0x07B, 0x8, 0x091, 0x8, 0x0C6,
            0x6, 0x02D, 0x9, 0x186, 0x8, 0x06F, 0x9, 0x093, 0xA, 0x1CC, 0x8, 0x05A, 0xA, 0x1AE, 0xA, 0x1C0,
            0x9, 0x148, 0x9, 0x14A, 0x9, 0x082, 0xA, 0x19F, 0x9, 0x171, 0x9, 0x120, 0x9, 0x0E7, 0xA, 0x1F3,
            0x9, 0x14B, 0x9, 0x100, 0x9, 0x190, 0x6, 0x013, 0x9, 0x161, 0x9, 0x125, 0x9, 0x133, 0x9, 0x195,
            0x9, 0x173, 0x9, 0x1CA, 0x9, 0x086, 0x9, 0x1E9, 0x9, 0x0DB, 0x9, 0x1EC, 0x9, 0x08B, 0x9, 0x085,
            0x5, 0x00A, 0x8, 0x096, 0x8, 0x09C, 0x9, 0x1C3, 0x9, 0x19C, 0x9, 0x08F, 0x9, 0x18F, 0x9, 0x091,
            0x9, 0x087, 0x9, 0x0C6, 0x9, 0x177, 0x9, 0x089, 0x9, 0x0D6, 0x9, 0x08C, 0x9, 0x1EE, 0x9, 0x1EB,
            0x9, 0x084, 0x9, 0x164, 0x9, 0x175, 0x9, 0x1CD, 0x8, 0x05E, 0x9, 0x088, 0x9, 0x12B, 0x9, 0x172,
            0x9, 0x10A, 0x9, 0x08D, 0x9, 0x13A, 0x9, 0x11C, 0xA, 0x1E1, 0xA, 0x1E0, 0x9, 0x187, 0xA, 0x1DC,
            0xA, 0x1DF, 0x7, 0x074, 0x9, 0x19F, 0x8, 0x08D, 0x8, 0x0E4, 0x7, 0x079, 0x9, 0x0EA, 0x9, 0x0E1,
            0x8, 0x040, 0x7, 0x041, 0x9, 0x10B, 0x9, 0x0B0, 0x8, 0x06A, 0x8, 0x0C1, 0x7, 0x071, 0x7, 0x078,
            0x8, 0x0B1, 0x9, 0x14C, 0x7, 0x043, 0x8, 0x076, 0x7, 0x066, 0x7, 0x04D, 0x9, 0x08A, 0x6, 0x02F,
            0x8, 0x0C9, 0x9, 0x0CE, 0x9, 0x149, 0x9, 0x160, 0xA, 0x1BA, 0xA, 0x19E, 0xA, 0x39F, 0x9, 0x0E5,
            0x9, 0x194, 0x9, 0x184, 0x9, 0x126, 0x7, 0x030, 0x8, 0x06C, 0x9, 0x121, 0x9, 0x1E8, 0xA, 0x1C1,
            0xA, 0x11D, 0xA, 0x163, 0xA, 0x385, 0xA, 0x3DB, 0xA, 0x17D, 0xA, 0x106, 0xA, 0x397, 0xA, 0x24E,
            0x7, 0x02E, 0x8, 0x098, 0xA, 0x33C, 0xA, 0x32E, 0xA, 0x1E9, 0x9, 0x0BF, 0xA, 0x3DF, 0xA, 0x1DD,
            0xA, 0x32D, 0xA, 0x2ED, 0xA, 0x30B, 0xA, 0x107, 0xA, 0x2E8, 0xA, 0x3DE, 0xA, 0x125, 0xA, 0x1E8,
            0x9, 0x0E9, 0xA, 0x1CD, 0xA, 0x1B5, 0x9, 0x165, 0xA, 0x232, 0xA, 0x2E1, 0xB, 0x3AE, 0xB, 0x3C6,
            0xB, 0x3E2, 0xA, 0x205, 0xA, 0x29A, 0xA, 0x248, 0xA, 0x2CD, 0xA, 0x23B, 0xB, 0x3C5, 0xA, 0x251,
            0xA, 0x2E9, 0xA, 0x252, 0x9, 0x1EA, 0xB, 0x3A0, 0xB, 0x391, 0xA, 0x23C, 0xB, 0x392, 0xB, 0x3D5,
            0xA, 0x233, 0xA, 0x2CC, 0xB, 0x390, 0xA, 0x1BB, 0xB, 0x3A1, 0xB, 0x3C4, 0xA, 0x211, 0xA, 0x203,
            0x9, 0x12A, 0xA, 0x231, 0xB, 0x3E0, 0xA, 0x29B, 0xB, 0x3D7, 0xA, 0x202, 0xB, 0x3AD, 0xA, 0x213,
            0xA, 0x253, 0xA, 0x32C, 0xA, 0x23D, 0xA, 0x23F, 0xA, 0x32F, 0xA, 0x11C, 0xA, 0x384, 0xA, 0x31C,
            0xA, 0x17C, 0xA, 0x30A, 0xA, 0x2E0, 0xA, 0x276, 0xA, 0x250, 0xB, 0x3E3, 0xA, 0x396, 0xA, 0x18F,
            0xA, 0x204, 0xA, 0x206, 0xA, 0x230, 0xA, 0x265, 0xA, 0x212, 0xA, 0x23E, 0xB, 0x3AC, 0xB, 0x393,
            0xB, 0x3E1, 0xA, 0x1DE, 0xB, 0x3D6, 0xA, 0x31D, 0xB, 0x3E5, 0xB, 0x3E4, 0xA, 0x207, 0xB, 0x3C7,
            0xA, 0x277, 0xB, 0x3D4, 0x8, 0x0C0, 0xA, 0x162, 0xA, 0x3DA, 0xA, 0x124, 0xA, 0x1B4, 0xA, 0x264,
            0xA, 0x33D, 0xA, 0x1D1, 0xA, 0x1AF, 0xA, 0x39E, 0xA, 0x24F, 0xB, 0x373, 0xA, 0x249, 0xB, 0x372,
            0x9, 0x167, 0xA, 0x210, 0xA, 0x23A, 0xA, 0x1B8, 0xB, 0x3AF, 0xA, 0x18E, 0xA, 0x2EC, 0x7, 0x062,
            0x4, 0x00D
        };

        private const int CountIndex = 0;
        private const int ValueIndex = 1;

        // UO packets may not exceed 64kb in length
        private const int BufferSize = 0x10000;

        // Optimal compression ratio is 2 / 8;  worst compression ratio is 11 / 8
        private const int MinimalCodeLength = 2;
        private const int MaximalCodeLength = 11;

        // Fixed overhead, in bits, per compression call
        private const int TerminalCodeLength = 4;

        // If our input exceeds this length, we cannot possibly compress it within the buffer
        private const int DefiniteOverflow = ((BufferSize * 8) - TerminalCodeLength) / MinimalCodeLength;

        // If our input exceeds this length, we may potentially overflow the buffer
        private const int PossibleOverflow = ((BufferSize * 8) - TerminalCodeLength) / MaximalCodeLength;

        public unsafe static void Compress(byte[] input, int offset, int count, byte[] output, ref int length)
        {
            if (input == null)
            {
                throw new ArgumentNullException("input");
            }
            else if (offset < 0 || offset >= input.Length)
            {
                throw new ArgumentOutOfRangeException("offset");
            }
            else if (count < 0 || count > input.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            else if ((input.Length - offset) < count)
            {
                throw new ArgumentException();
            }

            length = 0;

            if (count > DefiniteOverflow)
            {
                return;
            }

            int bitCount = 0;
            int bitValue = 0;

            fixed (int* pTable = _huffmanTable)
            {
                int* pEntry;

                fixed (byte* pInputBuffer = input)
                {
                    byte* pInput = pInputBuffer + offset, pInputEnd = pInput + count;

                    fixed (byte* pOutputBuffer = output)
                    {
                        byte* pOutput = pOutputBuffer, pOutputEnd = pOutput + BufferSize;

                        while (pInput < pInputEnd)
                        {
                            pEntry = &pTable[*pInput++ << 1];

                            bitCount += pEntry[CountIndex];

                            bitValue <<= pEntry[CountIndex];
                            bitValue |= pEntry[ValueIndex];

                            while (bitCount >= 8)
                            {
                                bitCount -= 8;

                                if (pOutput < pOutputEnd)
                                {
                                    *pOutput++ = (byte)(bitValue >> bitCount);
                                }
                                else
                                {
                                    length = 0;
                                    return;
                                }
                            }
                        }

                        // terminal code
                        pEntry = &pTable[0x200];

                        bitCount += pEntry[CountIndex];

                        bitValue <<= pEntry[CountIndex];
                        bitValue |= pEntry[ValueIndex];

                        // align on byte boundary
                        if ((bitCount & 7) != 0)
                        {
                            bitValue <<= (8 - (bitCount & 7));
                            bitCount += (8 - (bitCount & 7));
                        }

                        while (bitCount >= 8)
                        {
                            bitCount -= 8;

                            if (pOutput < pOutputEnd)
                            {
                                *pOutput++ = (byte)(bitValue >> bitCount);
                            }
                            else
                            {
                                length = 0;
                                return;
                            }
                        }

                        length = (int)(pOutput - pOutputBuffer);
                        return;
                    }
                }
            }
        }

        public static readonly ICompressor Compressor;

        static Compression()
        {
            if (Core.Unix)
            {
                if (Core.Is64Bit)
                {
                    Compressor = new CompressorUnix64();
                }
                else
                {
                    Compressor = new CompressorUnix32();
                }
            }
            else if (Core.Is64Bit)
            {
                Compressor = new Compressor64();
            }
            else
            {
                Compressor = new Compressor32();
            }
        }

        public static ZLibError Pack(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return Compressor.Compress(dest, ref destLength, source, sourceLength);
        }

        public static ZLibError Pack(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
        {
            return Compressor.Compress(dest, ref destLength, source, sourceLength, quality);
        }

        public static ZLibError Unpack(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return Compressor.Decompress(dest, ref destLength, source, sourceLength);
        }
    }

    public interface ICompressor
    {
        string Version
        {
            get;
        }

        ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength);
        ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality);

        ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength);
    }

    public sealed class Compressor32 : ICompressor
    {
        internal class SafeNativeMethods
        {
            [DllImport("zlib32")]
            internal static extern string zlibVersion();

            [DllImport("zlib32")]
            internal static extern ZLibError compress(byte[] dest, ref int destLength, byte[] source, int sourceLength);

            [DllImport("zlib32")]
            internal static extern ZLibError compress2(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality);

            [DllImport("zlib32")]
            internal static extern ZLibError uncompress(byte[] dest, ref int destLen, byte[] source, int sourceLen);
        }

        public Compressor32()
        {
        }

        public string Version
        {
            get
            {
                return SafeNativeMethods.zlibVersion();
            }
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return SafeNativeMethods.compress(dest, ref destLength, source, sourceLength);
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
        {
            return SafeNativeMethods.compress2(dest, ref destLength, source, sourceLength, quality);
        }

        public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return SafeNativeMethods.uncompress(dest, ref destLength, source, sourceLength);
        }
    }

    public sealed class Compressor64 : ICompressor
    {
        internal class SafeNativeMethods
        {
            [DllImport("zlib64")]
            internal static extern string zlibVersion();

            [DllImport("zlib64")]
            internal static extern ZLibError compress(byte[] dest, ref int destLength, byte[] source, int sourceLength);

            [DllImport("zlib64")]
            internal static extern ZLibError compress2(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality);

            [DllImport("zlib64")]
            internal static extern ZLibError uncompress(byte[] dest, ref int destLen, byte[] source, int sourceLen);
        }

        public Compressor64()
        {
        }

        public string Version
        {
            get
            {
                return SafeNativeMethods.zlibVersion();
            }
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return SafeNativeMethods.compress(dest, ref destLength, source, sourceLength);
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
        {
            return SafeNativeMethods.compress2(dest, ref destLength, source, sourceLength, quality);
        }

        public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return SafeNativeMethods.uncompress(dest, ref destLength, source, sourceLength);
        }
    }

    public sealed class CompressorUnix32 : ICompressor
    {
        internal class SafeNativeMethods
        {
            [DllImport("libz")]
            internal static extern string zlibVersion();

            [DllImport("libz")]
            internal static extern ZLibError compress(byte[] dest, ref int destLength, byte[] source, int sourceLength);

            [DllImport("libz")]
            internal static extern ZLibError compress2(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality);

            [DllImport("libz")]
            internal static extern ZLibError uncompress(byte[] dest, ref int destLen, byte[] source, int sourceLen);
        }

        public CompressorUnix32()
        {
        }

        public string Version
        {
            get
            {
                return SafeNativeMethods.zlibVersion();
            }
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return SafeNativeMethods.compress(dest, ref destLength, source, sourceLength);
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
        {
            return SafeNativeMethods.compress2(dest, ref destLength, source, sourceLength, quality);
        }

        public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            return SafeNativeMethods.uncompress(dest, ref destLength, source, sourceLength);
        }
    }

    public sealed class CompressorUnix64 : ICompressor
    {
        internal class SafeNativeMethods
        {
            [DllImport("libz")]
            internal static extern string zlibVersion();

            [DllImport("libz")]
            internal static extern ZLibError compress(byte[] dest, ref ulong destLength, byte[] source, int sourceLength);

            [DllImport("libz")]
            internal static extern ZLibError compress2(byte[] dest, ref ulong destLength, byte[] source, int sourceLength, ZLibQuality quality);

            [DllImport("libz")]
            internal static extern ZLibError uncompress(byte[] dest, ref ulong destLen, byte[] source, int sourceLen);
        }

        public CompressorUnix64()
        {
        }

        public string Version
        {
            get
            {
                return SafeNativeMethods.zlibVersion();
            }
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            ulong destLengthLong = (ulong)destLength;
            ZLibError z = SafeNativeMethods.compress(dest, ref destLengthLong, source, sourceLength);
            destLength = (int)destLengthLong;
            return z;
        }

        public ZLibError Compress(byte[] dest, ref int destLength, byte[] source, int sourceLength, ZLibQuality quality)
        {
            ulong destLengthLong = (ulong)destLength;
            ZLibError z = SafeNativeMethods.compress2(dest, ref destLengthLong, source, sourceLength, quality);
            destLength = (int)destLengthLong;
            return z;
        }

        public ZLibError Decompress(byte[] dest, ref int destLength, byte[] source, int sourceLength)
        {
            ulong destLengthLong = (ulong)destLength;
            ZLibError z = SafeNativeMethods.uncompress(dest, ref destLengthLong, source, sourceLength);
            destLength = (int)destLengthLong;
            return z;
        }
    }

    public enum ZLibError : int
    {
        VersionError = -6,
        BufferError = -5,
        MemoryError = -4,
        DataError = -3,
        StreamError = -2,
        FileError = -1,

        Okay = 0,

        StreamEnd = 1,
        NeedDictionary = 2
    }

    public enum ZLibQuality : int
    {
        Default = -1,

        None = 0,

        Speed = 1,
        Size = 9
    }

    public delegate void OnEncodedPacketReceive(NetState state, IEntity ent, EncodedReader pvSrc);

    public class EncodedPacketHandler
    {
        private int m_PacketID;
        private bool m_Ingame;
        private OnEncodedPacketReceive m_OnReceive;

        public EncodedPacketHandler(int packetID, bool ingame, OnEncodedPacketReceive onReceive)
        {
            m_PacketID = packetID;
            m_Ingame = ingame;
            m_OnReceive = onReceive;
        }

        public int PacketID
        {
            get
            {
                return m_PacketID;
            }
        }

        public OnEncodedPacketReceive OnReceive
        {
            get
            {
                return m_OnReceive;
            }
        }

        public bool Ingame
        {
            get
            {
                return m_Ingame;
            }
        }
    }

    public class EncodedReader
    {
        private PacketReader m_Reader;

        public EncodedReader(PacketReader reader)
        {
            m_Reader = reader;
        }

        public byte[] Buffer
        {
            get
            {
                return m_Reader.Buffer;
            }
        }

        public void Trace(NetState state)
        {
            m_Reader.Trace(state);
        }

        public int ReadInt32()
        {
            if (m_Reader.ReadByte() != 0)
                return 0;

            return m_Reader.ReadInt32();
        }

        public Point3D ReadPoint3D()
        {
            if (m_Reader.ReadByte() != 3)
                return Point3D.Zero;

            return new Point3D(m_Reader.ReadInt16(), m_Reader.ReadInt16(), m_Reader.ReadByte());
        }

        public string ReadUnicodeStringSafe()
        {
            if (m_Reader.ReadByte() != 2)
                return "";

            int length = m_Reader.ReadUInt16();

            return m_Reader.ReadUnicodeStringSafe(length);
        }

        public string ReadUnicodeString()
        {
            if (m_Reader.ReadByte() != 2)
                return "";

            int length = m_Reader.ReadUInt16();

            return m_Reader.ReadUnicodeString(length);
        }
    }

    public class Listener : IDisposable
    {
        private Socket m_Listener;

        private Queue<Socket> m_Accepted;
        private object m_AcceptedSyncRoot;

#if NewAsyncSockets
		private SocketAsyncEventArgs m_EventArgs;
#else
        private AsyncCallback m_OnAccept;
#endif

        private static Socket[] m_EmptySockets = new Socket[0];

        private static IPEndPoint[] m_EndPoints;

        public static IPEndPoint[] EndPoints
        {
            get { return m_EndPoints; }
            set { m_EndPoints = value; }
        }

        public Listener(IPEndPoint ipep)
        {
            m_Accepted = new Queue<Socket>();
            m_AcceptedSyncRoot = ((ICollection)m_Accepted).SyncRoot;

            m_Listener = Bind(ipep);

            if (m_Listener == null)
                return;

            DisplayListener();

#if NewAsyncSockets
			m_EventArgs = new SocketAsyncEventArgs();
			m_EventArgs.Completed += new EventHandler<SocketAsyncEventArgs>( Accept_Completion );
			Accept_Start();
#else
            m_OnAccept = new AsyncCallback(OnAccept);
            try
            {
                IAsyncResult res = m_Listener.BeginAccept(m_OnAccept, m_Listener);
            }
            catch (SocketException ex)
            {
                NetState.TraceException(ex);
            }
            catch (ObjectDisposedException)
            {
            }
#endif
        }

        private Socket Bind(IPEndPoint ipep)
        {
            Socket s = new Socket(ipep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                s.LingerState.Enabled = false;
                s.ExclusiveAddressUse = false;

                s.Bind(ipep);
                s.Listen(8);

                return s;
            }
            catch (Exception e)
            {
                if (e is SocketException)
                {
                    SocketException se = (SocketException)e;

                    if (se.ErrorCode == 10048)
                    { // WSAEADDRINUSE
                        Console.WriteLine("Listener Failed: {0}:{1} (In Use)", ipep.Address, ipep.Port);
                    }
                    else if (se.ErrorCode == 10049)
                    { // WSAEADDRNOTAVAIL
                        Console.WriteLine("Listener Failed: {0}:{1} (Unavailable)", ipep.Address, ipep.Port);
                    }
                    else
                    {
                        Console.WriteLine("Listener Exception:");
                        Console.WriteLine(e);
                    }
                }

                return null;
            }
        }

        private void DisplayListener()
        {
            IPEndPoint ipep = m_Listener.LocalEndPoint as IPEndPoint;

            if (ipep == null)
                return;

            if (ipep.Address.Equals(IPAddress.Any) || ipep.Address.Equals(IPAddress.IPv6Any))
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface adapter in adapters)
                {
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    foreach (IPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        if (ipep.AddressFamily == unicast.Address.AddressFamily)
                            Console.WriteLine("Listening: {0}:{1}", unicast.Address, ipep.Port);
                    }
                }
                /*
				try {
					Console.WriteLine( "Listening: {0}:{1}", IPAddress.Loopback, ipep.Port );
					IPHostEntry iphe = Dns.GetHostEntry( Dns.GetHostName() );
					IPAddress[] ip = iphe.AddressList;
					for ( int i = 0; i < ip.Length; ++i )
						Console.WriteLine( "Listening: {0}:{1}", ip[i], ipep.Port );
				}
				catch { }
				*/
            }
            else
            {
                Console.WriteLine("Listening: {0}:{1}", ipep.Address, ipep.Port);
            }
        }

#if NewAsyncSockets
		private void Accept_Start()
		{
			bool result = false;

			do {
				try {
					result = !m_Listener.AcceptAsync( m_EventArgs );
				} catch ( SocketException ex ) {
					NetState.TraceException( ex );
					break;
				} catch ( ObjectDisposedException ) {
					break;
				}

				if ( result )
					Accept_Process( m_EventArgs );
			} while ( result );
		}

		private void Accept_Completion( object sender, SocketAsyncEventArgs e )
		{
			Accept_Process( e );

			Accept_Start();
		}

		private void Accept_Process( SocketAsyncEventArgs e )
		{
			if ( e.SocketError == SocketError.Success && VerifySocket( e.AcceptSocket ) ) {
				Enqueue( e.AcceptSocket );
			} else {
				Release( e.AcceptSocket );
			}

			e.AcceptSocket = null;
		}

#else

        private void OnAccept(IAsyncResult asyncResult)
        {
            Socket listener = (Socket)asyncResult.AsyncState;

            Socket accepted = null;

            try
            {
                accepted = listener.EndAccept(asyncResult);
            }
            catch (SocketException ex)
            {
                NetState.TraceException(ex);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (accepted != null)
            {
                if (VerifySocket(accepted))
                {
                    Enqueue(accepted);
                }
                else
                {
                    Release(accepted);
                }
            }

            try
            {
                listener.BeginAccept(m_OnAccept, listener);
            }
            catch (SocketException ex)
            {
                NetState.TraceException(ex);
            }
            catch (ObjectDisposedException)
            {
            }
        }
#endif

        private bool VerifySocket(Socket socket)
        {
            try
            {
                SocketConnectEventArgs args = new SocketConnectEventArgs(socket);

                EventSink.InvokeSocketConnect(args);

                return args.AllowConnection;
            }
            catch (Exception ex)
            {
                NetState.TraceException(ex);

                return false;
            }
        }

        private void Enqueue(Socket socket)
        {
            lock (m_AcceptedSyncRoot)
            {
                m_Accepted.Enqueue(socket);
            }

            Core.Set();
        }

        private void Release(Socket socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException ex)
            {
                NetState.TraceException(ex);
            }

            try
            {
                socket.Close();
            }
            catch (SocketException ex)
            {
                NetState.TraceException(ex);
            }
        }

        public Socket[] Slice()
        {
            Socket[] array;

            lock (m_AcceptedSyncRoot)
            {
                if (m_Accepted.Count == 0)
                    return m_EmptySockets;

                array = m_Accepted.ToArray();
                m_Accepted.Clear();
            }

            return array;
        }

        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                Socket socket = Interlocked.Exchange<Socket>(ref m_Listener, null);

                if (socket != null)
                {
                    socket.Close();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public class MessagePump
    {
        private Listener[] m_Listeners;
        private Queue<NetState> m_Queue;
        private Queue<NetState> m_WorkingQueue;
        private Queue<NetState> m_Throttled;

        public MessagePump()
        {
            IPEndPoint[] ipep = Listener.EndPoints;

            m_Listeners = new Listener[ipep.Length];

            bool success = false;

            do
            {
                for (int i = 0; i < ipep.Length; i++)
                {
                    Listener l = new Listener(ipep[i]);
                    if (!success && l != null)
                        success = true;
                    m_Listeners[i] = l;
                }

                if (!success)
                {
                    Console.WriteLine("Retrying...");
                    Thread.Sleep(10000);
                }
            } while (!success);

            m_Queue = new Queue<NetState>();
            m_WorkingQueue = new Queue<NetState>();
            m_Throttled = new Queue<NetState>();
        }

        public Listener[] Listeners
        {
            get { return m_Listeners; }
            set { m_Listeners = value; }
        }

        public void AddListener(Listener l)
        {
            Listener[] old = m_Listeners;

            m_Listeners = new Listener[old.Length + 1];

            for (int i = 0; i < old.Length; ++i)
                m_Listeners[i] = old[i];

            m_Listeners[old.Length] = l;
        }

        private void CheckListener()
        {
            for (int j = 0; j < m_Listeners.Length; ++j)
            {
                Socket[] accepted = m_Listeners[j].Slice();

                for (int i = 0; i < accepted.Length; ++i)
                {
                    NetState ns = new NetState(accepted[i], this);
                    ns.Start();

                    if (ns.Running)
                        Console.WriteLine("Client: {0}: Connected. [{1} Online]", ns, NetState.Instances.Count);
                }
            }
        }

        public void OnReceive(NetState ns)
        {
            lock (this)
                m_Queue.Enqueue(ns);

            Core.Set();
        }

        public void Slice()
        {
            CheckListener();

            lock (this)
            {
                Queue<NetState> temp = m_WorkingQueue;
                m_WorkingQueue = m_Queue;
                m_Queue = temp;
            }

            while (m_WorkingQueue.Count > 0)
            {
                NetState ns = m_WorkingQueue.Dequeue();

                if (ns.Running)
                    HandleReceive(ns);
            }

            lock (this)
            {
                while (m_Throttled.Count > 0)
                    m_Queue.Enqueue(m_Throttled.Dequeue());
            }
        }

        private const int BufferSize = 4096;
        private BufferPool m_Buffers = new BufferPool("Processor", 4, BufferSize);

        private bool HandleSeed(NetState ns, ByteQueue buffer)
        {
            if (buffer.GetPacketID() == 0xEF)
            {
                // new packet in client	6.0.5.0	replaces the traditional seed method with a	seed packet
                // 0xEF	= 239 =	multicast IP, so this should never appear in a normal seed.	 So	this is	backwards compatible with older	clients.
                ns.Seeded = true;
                return true;
            }
            else if (buffer.Length >= 4)
            {
                byte[] m_Peek = new byte[4];

                buffer.Dequeue(m_Peek, 0, 4);

                int seed = (m_Peek[0] << 24) | (m_Peek[1] << 16) | (m_Peek[2] << 8) | m_Peek[3];

                if (seed == 0)
                {
                    Console.WriteLine("Login: {0}: Invalid client detected, disconnecting", ns);
                    ns.Dispose();
                    return false;
                }

                ns.m_Seed = seed;
                ns.Seeded = true;
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool CheckEncrypted(NetState ns, int packetID)
        {
            if (!ns.SentFirstPacket && packetID != 0xF0 && packetID != 0xF1 && packetID != 0xCF && packetID != 0x80 && packetID != 0x91 && packetID != 0xA4 && packetID != 0xEF)
            {
                Console.WriteLine("Client: {0}: Encrypted client detected, disconnecting", ns);
                ns.Dispose();
                return true;
            }
            return false;
        }

        public void HandleReceive(NetState ns)
        {
            ByteQueue buffer = ns.Buffer;

            if (buffer == null || buffer.Length <= 0)
                return;

            lock (buffer)
            {
                if (!ns.Seeded)
                {
                    if (!HandleSeed(ns, buffer))
                        return;
                }

                int length = buffer.Length;

                while (length > 0 && ns.Running)
                {
                    int packetID = buffer.GetPacketID();

                    if (CheckEncrypted(ns, packetID))
                        break;

                    PacketHandler handler = ns.GetHandler(packetID);

                    if (handler == null)
                    {
                        byte[] data = new byte[length];
                        length = buffer.Dequeue(data, 0, length);
                        new PacketReader(data, length, false).Trace(ns);
                        break;
                    }

                    int packetLength = handler.Length;

                    if (packetLength <= 0)
                    {
                        if (length >= 3)
                        {
                            packetLength = buffer.GetPacketLength();

                            if (packetLength < 3)
                            {
                                ns.Dispose();
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (length >= packetLength)
                    {
                        if (handler.Ingame)
                        {
                            if (ns.Mobile == null)
                            {
                                Console.WriteLine("Client: {0}: Sent ingame packet (0x{1:X2}) before having been attached to a mobile", ns, packetID);
                                ns.Dispose();
                                break;
                            }
                            else if (ns.Mobile.Deleted)
                            {
                                ns.Dispose();
                                break;
                            }
                        }

                        ThrottlePacketCallback throttler = handler.ThrottleCallback;

                        if (throttler != null && !throttler(ns))
                        {
                            m_Throttled.Enqueue(ns);
                            return;
                        }

                        PacketReceiveProfile prof = null;

                        if (Core.Profiling) prof = PacketReceiveProfile.Acquire(packetID);

                        if (prof != null)
                        {
                            prof.Start();
                        }

                        byte[] packetBuffer;

                        if (BufferSize >= packetLength)
                            packetBuffer = m_Buffers.AcquireBuffer();
                        else
                            packetBuffer = new byte[packetLength];

                        packetLength = buffer.Dequeue(packetBuffer, 0, packetLength);

                        PacketReader r = new PacketReader(packetBuffer, packetLength, handler.Length != 0);

                        handler.OnReceive(ns, r);
                        length = buffer.Length;

                        if (BufferSize >= packetLength)
                            m_Buffers.ReleaseBuffer(packetBuffer);

                        if (prof != null)
                        {
                            prof.Finish(packetLength);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
    }

    public interface IPacketEncoder
    {
        void EncodeOutgoingPacket(NetState to, ref byte[] buffer, ref int length);
        void DecodeIncomingPacket(NetState from, ref byte[] buffer, ref int length);
    }

    public delegate void NetStateCreatedCallback(NetState ns);

    public class NetState : IComparable<NetState>
    {
        private Socket m_Socket;
        private IPAddress m_Address;
        private ByteQueue m_Buffer;
        private byte[] m_RecvBuffer;
        private SendQueue m_SendQueue;
        private bool m_Seeded;
        private bool m_Running;

#if NewAsyncSockets
		private SocketAsyncEventArgs m_ReceiveEventArgs, m_SendEventArgs;
#else
        private AsyncCallback m_OnReceive, m_OnSend;
#endif

        private MessagePump m_MessagePump;
        private ServerInfo[] m_ServerInfo;
        private IAccount m_Account;
        private Mobile m_Mobile;
        private CityInfo[] m_CityInfo;
        private List<Gump> m_Gumps;
        private List<HuePicker> m_HuePickers;
        private List<IMenu> m_Menus;
        private List<SecureTrade> m_Trades;
        private int m_Sequence;
        private bool m_CompressionEnabled;
        private string m_ToString;
        private ClientVersion m_Version;
        private bool m_SentFirstPacket;
        private bool m_BlockAllPackets;

        private DateTime m_ConnectedOn;

        public DateTime ConnectedOn
        {
            get
            {
                return m_ConnectedOn;
            }
        }

        public TimeSpan ConnectedFor
        {
            get
            {
                return (DateTime.UtcNow - m_ConnectedOn);
            }
        }

        internal int m_Seed;
        internal int m_AuthID;

        public IPAddress Address
        {
            get
            {
                return m_Address;
            }
        }

        private ClientFlags m_Flags;

        private static bool m_Paused;

        [Flags]
        private enum AsyncState
        {
            Pending = 0x01,
            Paused = 0x02
        }

        private AsyncState m_AsyncState;
        private object m_AsyncLock = new object();

        private IPacketEncoder m_Encoder = null;

        public IPacketEncoder PacketEncoder
        {
            get
            {
                return m_Encoder;
            }
            set
            {
                m_Encoder = value;
            }
        }

        private static NetStateCreatedCallback m_CreatedCallback;

        public static NetStateCreatedCallback CreatedCallback
        {
            get
            {
                return m_CreatedCallback;
            }
            set
            {
                m_CreatedCallback = value;
            }
        }

        public bool SentFirstPacket
        {
            get
            {
                return m_SentFirstPacket;
            }
            set
            {
                m_SentFirstPacket = value;
            }
        }

        public bool BlockAllPackets
        {
            get
            {
                return m_BlockAllPackets;
            }
            set
            {
                m_BlockAllPackets = value;
            }
        }

        public ClientFlags Flags
        {
            get
            {
                return m_Flags;
            }
            set
            {
                m_Flags = value;
            }
        }

        public ClientVersion Version
        {
            get
            {
                return m_Version;
            }
            set
            {
                m_Version = value;

                if (value >= m_Version704565)
                {
                    _ProtocolChanges = ProtocolChanges.Version704565;
                }
                else if (value >= m_Version70331)
                {
                    _ProtocolChanges = ProtocolChanges.Version70331;
                }
                else if (value >= m_Version70300)
                {
                    _ProtocolChanges = ProtocolChanges.Version70300;
                }
                else if (value >= m_Version70160)
                {
                    _ProtocolChanges = ProtocolChanges.Version70160;
                }
                else if (value >= m_Version70130)
                {
                    _ProtocolChanges = ProtocolChanges.Version70130;
                }
                else if (value >= m_Version7090)
                {
                    _ProtocolChanges = ProtocolChanges.Version7090;
                }
                else if (value >= m_Version7000)
                {
                    _ProtocolChanges = ProtocolChanges.Version7000;
                }
                else if (value >= m_Version60142)
                {
                    _ProtocolChanges = ProtocolChanges.Version60142;
                }
                else if (value >= m_Version6017)
                {
                    _ProtocolChanges = ProtocolChanges.Version6017;
                }
                else if (value >= m_Version6000)
                {
                    _ProtocolChanges = ProtocolChanges.Version6000;
                }
                else if (value >= m_Version502b)
                {
                    _ProtocolChanges = ProtocolChanges.Version502b;
                }
                else if (value >= m_Version500a)
                {
                    _ProtocolChanges = ProtocolChanges.Version500a;
                }
                else if (value >= m_Version407a)
                {
                    _ProtocolChanges = ProtocolChanges.Version407a;
                }
                else if (value >= m_Version400a)
                {
                    _ProtocolChanges = ProtocolChanges.Version400a;
                }
            }
        }

        private static ClientVersion m_Version400a = new ClientVersion("4.0.0a");
        private static ClientVersion m_Version407a = new ClientVersion("4.0.7a");
        private static ClientVersion m_Version500a = new ClientVersion("5.0.0a");
        private static ClientVersion m_Version502b = new ClientVersion("5.0.2b");
        private static ClientVersion m_Version6000 = new ClientVersion("6.0.0.0");
        private static ClientVersion m_Version6017 = new ClientVersion("6.0.1.7");
        private static ClientVersion m_Version60142 = new ClientVersion("6.0.14.2");
        private static ClientVersion m_Version7000 = new ClientVersion("7.0.0.0");
        private static ClientVersion m_Version7090 = new ClientVersion("7.0.9.0");
        private static ClientVersion m_Version70130 = new ClientVersion("7.0.13.0");
        private static ClientVersion m_Version70160 = new ClientVersion("7.0.16.0");
        private static ClientVersion m_Version70300 = new ClientVersion("7.0.30.0");
        private static ClientVersion m_Version70331 = new ClientVersion("7.0.33.1");
        private static ClientVersion m_Version704565 = new ClientVersion("7.0.45.65");

        private ProtocolChanges _ProtocolChanges;

        private enum ProtocolChanges
        {
            NewSpellbook = 0x00000001,
            DamagePacket = 0x00000002,
            Unpack = 0x00000004,
            BuffIcon = 0x00000008,
            NewHaven = 0x00000010,
            ContainerGridLines = 0x00000020,
            ExtendedSupportedFeatures = 0x00000040,
            StygianAbyss = 0x00000080,
            HighSeas = 0x00000100,
            NewCharacterList = 0x00000200,
            NewCharacterCreation = 0x00000400,
            ExtendedStatus = 0x00000800,
            NewMobileIncoming = 0x00001000,
            NewSecureTrading = 0x00002000,

            Version400a = NewSpellbook,
            Version407a = Version400a | DamagePacket,
            Version500a = Version407a | Unpack,
            Version502b = Version500a | BuffIcon,
            Version6000 = Version502b | NewHaven,
            Version6017 = Version6000 | ContainerGridLines,
            Version60142 = Version6017 | ExtendedSupportedFeatures,
            Version7000 = Version60142 | StygianAbyss,
            Version7090 = Version7000 | HighSeas,
            Version70130 = Version7090 | NewCharacterList,
            Version70160 = Version70130 | NewCharacterCreation,
            Version70300 = Version70160 | ExtendedStatus,
            Version70331 = Version70300 | NewMobileIncoming,
            Version704565 = Version70331 | NewSecureTrading
        }

        public bool NewSpellbook { get { return ((_ProtocolChanges & ProtocolChanges.NewSpellbook) != 0); } }
        public bool DamagePacket { get { return ((_ProtocolChanges & ProtocolChanges.DamagePacket) != 0); } }
        public bool Unpack { get { return ((_ProtocolChanges & ProtocolChanges.Unpack) != 0); } }
        public bool BuffIcon { get { return ((_ProtocolChanges & ProtocolChanges.BuffIcon) != 0); } }
        public bool NewHaven { get { return ((_ProtocolChanges & ProtocolChanges.NewHaven) != 0); } }
        public bool ContainerGridLines { get { return ((_ProtocolChanges & ProtocolChanges.ContainerGridLines) != 0); } }
        public bool ExtendedSupportedFeatures { get { return ((_ProtocolChanges & ProtocolChanges.ExtendedSupportedFeatures) != 0); } }
        public bool StygianAbyss { get { return ((_ProtocolChanges & ProtocolChanges.StygianAbyss) != 0); } }
        public bool HighSeas { get { return ((_ProtocolChanges & ProtocolChanges.HighSeas) != 0); } }
        public bool NewCharacterList { get { return ((_ProtocolChanges & ProtocolChanges.NewCharacterList) != 0); } }
        public bool NewCharacterCreation { get { return ((_ProtocolChanges & ProtocolChanges.NewCharacterCreation) != 0); } }
        public bool ExtendedStatus { get { return ((_ProtocolChanges & ProtocolChanges.ExtendedStatus) != 0); } }
        public bool NewMobileIncoming { get { return ((_ProtocolChanges & ProtocolChanges.NewMobileIncoming) != 0); } }
        public bool NewSecureTrading { get { return ((_ProtocolChanges & ProtocolChanges.NewSecureTrading) != 0); } }

        public bool IsUOTDClient
        {
            get
            {
                return ((m_Flags & ClientFlags.UOTD) != 0 || (m_Version != null && m_Version.Type == ClientType.UOTD));
            }
        }

        public bool IsSAClient
        {
            get
            {
                return (m_Version != null && m_Version.Type == ClientType.SA);
            }
        }

        public List<SecureTrade> Trades
        {
            get
            {
                return m_Trades;
            }
        }

        public void ValidateAllTrades()
        {
            for (int i = m_Trades.Count - 1; i >= 0; --i)
            {
                if (i >= m_Trades.Count)
                {
                    continue;
                }

                SecureTrade trade = m_Trades[i];

                if (trade.From.Mobile.Deleted || trade.To.Mobile.Deleted || !trade.From.Mobile.Alive || !trade.To.Mobile.Alive || !trade.From.Mobile.InRange(trade.To.Mobile, 2) || trade.From.Mobile.Map != trade.To.Mobile.Map)
                {
                    trade.Cancel();
                }
            }
        }

        public void CancelAllTrades()
        {
            for (int i = m_Trades.Count - 1; i >= 0; --i)
            {
                if (i < m_Trades.Count)
                {
                    m_Trades[i].Cancel();
                }
            }
        }

        public void RemoveTrade(SecureTrade trade)
        {
            m_Trades.Remove(trade);
        }

        public SecureTrade FindTrade(Mobile m)
        {
            for (int i = 0; i < m_Trades.Count; ++i)
            {
                SecureTrade trade = m_Trades[i];

                if (trade.From.Mobile == m || trade.To.Mobile == m)
                {
                    return trade;
                }
            }

            return null;
        }

        public SecureTradeContainer FindTradeContainer(Mobile m)
        {
            for (int i = 0; i < m_Trades.Count; ++i)
            {
                SecureTrade trade = m_Trades[i];

                SecureTradeInfo from = trade.From;
                SecureTradeInfo to = trade.To;

                if (from.Mobile == m_Mobile && to.Mobile == m)
                {
                    return from.Container;
                }
                else if (from.Mobile == m && to.Mobile == m_Mobile)
                {
                    return to.Container;
                }
            }

            return null;
        }

        public SecureTradeContainer AddTrade(NetState state)
        {
            SecureTrade newTrade = new SecureTrade(m_Mobile, state.m_Mobile);

            m_Trades.Add(newTrade);
            state.m_Trades.Add(newTrade);

            return newTrade.From.Container;
        }

        public bool CompressionEnabled
        {
            get
            {
                return m_CompressionEnabled;
            }
            set
            {
                m_CompressionEnabled = value;
            }
        }

        public int Sequence
        {
            get
            {
                return m_Sequence;
            }
            set
            {
                m_Sequence = value;
            }
        }

        public List<Gump> Gumps
        {
            get
            {
                return m_Gumps;
            }
        }

        public List<HuePicker> HuePickers
        {
            get
            {
                return m_HuePickers;
            }
        }

        public List<IMenu> Menus
        {
            get
            {
                return m_Menus;
            }
        }

        private static int m_GumpCap = 512, m_HuePickerCap = 512, m_MenuCap = 512;

        public static int GumpCap
        {
            get
            {
                return m_GumpCap;
            }
            set
            {
                m_GumpCap = value;
            }
        }

        public static int HuePickerCap
        {
            get
            {
                return m_HuePickerCap;
            }
            set
            {
                m_HuePickerCap = value;
            }
        }

        public static int MenuCap
        {
            get
            {
                return m_MenuCap;
            }
            set
            {
                m_MenuCap = value;
            }
        }

        public void WriteConsole(string text)
        {
            Console.WriteLine("Client: {0}: {1}", this, text);
        }

        public void WriteConsole(string format, params object[] args)
        {
            WriteConsole(String.Format(format, args));
        }

        public void AddMenu(IMenu menu)
        {
            if (m_Menus == null)
            {
                m_Menus = new List<IMenu>();
            }

            if (m_Menus.Count < m_MenuCap)
            {
                m_Menus.Add(menu);
            }
            else
            {
                WriteConsole("Exceeded menu cap, disconnecting...");
                Dispose();
            }
        }

        public void RemoveMenu(IMenu menu)
        {
            if (m_Menus != null)
            {
                m_Menus.Remove(menu);
            }
        }

        public void RemoveMenu(int index)
        {
            if (m_Menus != null)
            {
                m_Menus.RemoveAt(index);
            }
        }

        public void ClearMenus()
        {
            if (m_Menus != null)
            {
                m_Menus.Clear();
            }
        }

        public void AddHuePicker(HuePicker huePicker)
        {
            if (m_HuePickers == null)
            {
                m_HuePickers = new List<HuePicker>();
            }

            if (m_HuePickers.Count < m_HuePickerCap)
            {
                m_HuePickers.Add(huePicker);
            }
            else
            {
                WriteConsole("Exceeded hue picker cap, disconnecting...");
                Dispose();
            }
        }

        public void RemoveHuePicker(HuePicker huePicker)
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.Remove(huePicker);
            }
        }

        public void RemoveHuePicker(int index)
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.RemoveAt(index);
            }
        }

        public void ClearHuePickers()
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.Clear();
            }
        }

        public void AddGump(Gump gump)
        {
            if (m_Gumps == null)
            {
                m_Gumps = new List<Gump>();
            }

            if (m_Gumps.Count < m_GumpCap)
            {
                m_Gumps.Add(gump);
            }
            else
            {
                WriteConsole("Exceeded gump cap, disconnecting...");
                Dispose();
            }
        }

        public void RemoveGump(Gump gump)
        {
            if (m_Gumps != null)
            {
                m_Gumps.Remove(gump);
            }
        }

        public void RemoveGump(int index)
        {
            if (m_Gumps != null)
            {
                m_Gumps.RemoveAt(index);
            }
        }

        public void ClearGumps()
        {
            if (m_Gumps != null)
            {
                m_Gumps.Clear();
            }
        }

        public void LaunchBrowser(string url)
        {
            Send(new MessageLocalized(Serial.MinusOne, -1, MessageType.Label, 0x35, 3, 501231, "", ""));
            Send(new LaunchBrowser(url));
        }

        public CityInfo[] CityInfo
        {
            get
            {
                return m_CityInfo;
            }
            set
            {
                m_CityInfo = value;
            }
        }

        public Mobile Mobile
        {
            get
            {
                return m_Mobile;
            }
            set
            {
                m_Mobile = value;
            }
        }

        public ServerInfo[] ServerInfo
        {
            get
            {
                return m_ServerInfo;
            }
            set
            {
                m_ServerInfo = value;
            }
        }

        public IAccount Account
        {
            get
            {
                return m_Account;
            }
            set
            {
                m_Account = value;
            }
        }

        public override string ToString()
        {
            return m_ToString;
        }

        private static List<NetState> m_Instances = new List<NetState>();

        public static List<NetState> Instances
        {
            get
            {
                return m_Instances;
            }
        }

        private static BufferPool m_ReceiveBufferPool = new BufferPool("Receive", 2048, 2048);

        public NetState(Socket socket, MessagePump messagePump)
        {
            m_Socket = socket;
            m_Buffer = new ByteQueue();
            m_Seeded = false;
            m_Running = false;
            m_RecvBuffer = m_ReceiveBufferPool.AcquireBuffer();
            m_MessagePump = messagePump;
            m_Gumps = new List<Gump>();
            m_HuePickers = new List<HuePicker>();
            m_Menus = new List<IMenu>();
            m_Trades = new List<SecureTrade>();

            m_SendQueue = new SendQueue();

            m_NextCheckActivity = Core.TickCount + 30000;

            m_Instances.Add(this);

            try
            {
                m_Address = Utility.Intern(((IPEndPoint)m_Socket.RemoteEndPoint).Address);
                m_ToString = m_Address.ToString();
            }
            catch (Exception ex)
            {
                TraceException(ex);
                m_Address = IPAddress.None;
                m_ToString = "(error)";
            }

            m_ConnectedOn = DateTime.UtcNow;

            if (m_CreatedCallback != null)
            {
                m_CreatedCallback(this);
            }
        }

        private bool _sending;
        private object _sendL = new object();

        public virtual void Send(Packet p)
        {
            if (m_Socket == null || m_BlockAllPackets)
            {
                p.OnSend();
                return;
            }

            int length;
            byte[] buffer = p.Compile(m_CompressionEnabled, out length);

            if (buffer != null)
            {
                if (buffer.Length <= 0 || length <= 0)
                {
                    p.OnSend();
                    return;
                }

                PacketSendProfile prof = null;

                if (Core.Profiling) prof = PacketSendProfile.Acquire(p.GetType());

                if (prof != null)
                {
                    prof.Start();
                }

                if (m_Encoder != null)
                {
                    m_Encoder.EncodeOutgoingPacket(this, ref buffer, ref length);
                }

                try
                {
                    SendQueue.Gram gram;

                    lock (_sendL)
                    {
                        lock (m_SendQueue)
                            gram = m_SendQueue.Enqueue(buffer, length);

                        if (gram != null && !_sending)
                        {
                            _sending = true;
#if NewAsyncSockets
							m_SendEventArgs.SetBuffer( gram.Buffer, 0, gram.Length );
							Send_Start();
#else
                            try
                            {
                                m_Socket.BeginSend(gram.Buffer, 0, gram.Length, SocketFlags.None, m_OnSend, m_Socket);
                            }
                            catch (Exception ex)
                            {
                                TraceException(ex);
                                Dispose(false);
                            }
#endif
                        }
                    }
                }
                catch (CapacityExceededException)
                {
                    Console.WriteLine("Client: {0}: Too much data pending, disconnecting...", this);
                    Dispose(false);
                }

                p.OnSend();

                if (prof != null)
                {
                    prof.Finish(length);
                }
            }
            else
            {
                Console.WriteLine("Client: {0}: null buffer send, disconnecting...", this);
                using (StreamWriter op = new StreamWriter("null_send.log", true))
                {
                    op.WriteLine("{0} Client: {1}: null buffer send, disconnecting...", DateTime.UtcNow, this);
                    op.WriteLine(new System.Diagnostics.StackTrace());
                }
                Dispose();
            }
        }

#if NewAsyncSockets
		public void Start() {
			m_ReceiveEventArgs = new SocketAsyncEventArgs();
			m_ReceiveEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>( Receive_Completion );
			m_ReceiveEventArgs.SetBuffer( m_RecvBuffer, 0, m_RecvBuffer.Length );

			m_SendEventArgs = new SocketAsyncEventArgs();
			m_SendEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>( Send_Completion );

			m_Running = true;

			if ( m_Socket == null || m_Paused ) {
				return;
			}

			Receive_Start();
		}

		private void Receive_Start()
		{
			try {
				bool result = false;

				do {
					lock ( m_AsyncLock ) {
						if ( ( m_AsyncState & ( AsyncState.Pending | AsyncState.Paused ) ) == 0 ) {
							m_AsyncState |= AsyncState.Pending;
							result = !m_Socket.ReceiveAsync( m_ReceiveEventArgs );

							if ( result )
								Receive_Process( m_ReceiveEventArgs );
						}
					}
				} while ( result );
			} catch ( Exception ex ) {
				TraceException( ex );
				Dispose( false );
			}
		}

		private void Receive_Completion( object sender, SocketAsyncEventArgs e )
		{
			Receive_Process( e );

			if ( !m_Disposing )
				Receive_Start();
		}

		private void Receive_Process( SocketAsyncEventArgs e )
		{
			int byteCount = e.BytesTransferred;

			if ( e.SocketError != SocketError.Success || byteCount <= 0 ) {
				Dispose( false );
				return;
			} else if ( m_Disposing ) {
				return;
			}

			m_NextCheckActivity = Core.TickCount + 90000;

			byte[] buffer = m_RecvBuffer;

			if ( m_Encoder != null )
				m_Encoder.DecodeIncomingPacket( this, ref buffer, ref byteCount );

			lock ( m_Buffer )
				m_Buffer.Enqueue( buffer, 0, byteCount );

			m_MessagePump.OnReceive( this );

			lock ( m_AsyncLock ) {
				m_AsyncState &= ~AsyncState.Pending;
			}
		}

		private void Send_Start()
		{
			try {
				bool result = false;

				do {
					result = !m_Socket.SendAsync( m_SendEventArgs );

					if ( result )
						Send_Process( m_SendEventArgs );
				} while ( result ); 
			} catch ( Exception ex ) {
				TraceException( ex );
				Dispose( false );
			}
		}

		private void Send_Completion( object sender, SocketAsyncEventArgs e )
		{
			Send_Process( e );

			if ( m_Disposing )
				return;

			if ( m_CoalesceSleep >= 0 ) {
				Thread.Sleep( m_CoalesceSleep );
			}

			SendQueue.Gram gram;

			lock ( m_SendQueue ) {
				gram = m_SendQueue.Dequeue();

				if (gram == null && m_SendQueue.IsFlushReady)
					gram = m_SendQueue.CheckFlushReady();
			}

			if ( gram != null ) {
				m_SendEventArgs.SetBuffer( gram.Buffer, 0, gram.Length );
				Send_Start();
			} else {
				lock (_sendL)
					_sending = false;
			}
		}

		private void Send_Process( SocketAsyncEventArgs e )
		{
			int bytes = e.BytesTransferred;

			if ( e.SocketError != SocketError.Success || bytes <= 0 ) {
				Dispose( false );
				return;
			}

			m_NextCheckActivity = Core.TickCount + 90000;
		}

		public static void Pause() {
			m_Paused = true;

			for ( int i = 0; i < m_Instances.Count; ++i ) {
				NetState ns = m_Instances[i];

				lock ( ns.m_AsyncLock ) {
					ns.m_AsyncState |= AsyncState.Paused;
				}
			}
		}

		public static void Resume() {
			m_Paused = false;

			for ( int i = 0; i < m_Instances.Count; ++i ) {
				NetState ns = m_Instances[i];

				if ( ns.m_Socket == null ) {
					continue;
				}

				lock ( ns.m_AsyncLock ) {
					ns.m_AsyncState &= ~AsyncState.Paused;

					if ( ( ns.m_AsyncState & AsyncState.Pending ) == 0 )
						ns.Receive_Start();
				}
			}
		}

		public bool Flush() {
			if ( m_Socket == null )
					return false;

			lock (_sendL) {
				if (_sending)
					return false;

				SendQueue.Gram gram;

				lock ( m_SendQueue ) {
					if (!m_SendQueue.IsFlushReady)
						return false;

					gram = m_SendQueue.CheckFlushReady();
				}

				if ( gram != null ) {
					_sending = true;
					m_SendEventArgs.SetBuffer( gram.Buffer, 0, gram.Length );
					Send_Start();
				}
			}

			return false;
		}

#else

        public void Start()
        {
            m_OnReceive = new AsyncCallback(OnReceive);
            m_OnSend = new AsyncCallback(OnSend);

            m_Running = true;

            if (m_Socket == null || m_Paused)
            {
                return;
            }

            try
            {
                lock (m_AsyncLock)
                {
                    if ((m_AsyncState & (AsyncState.Pending | AsyncState.Paused)) == 0)
                    {
                        InternalBeginReceive();
                    }
                }
            }
            catch (Exception ex)
            {
                TraceException(ex);
                Dispose(false);
            }
        }

        private void InternalBeginReceive()
        {
            m_AsyncState |= AsyncState.Pending;

            m_Socket.BeginReceive(m_RecvBuffer, 0, m_RecvBuffer.Length, SocketFlags.None, m_OnReceive, m_Socket);
        }

        private void OnReceive(IAsyncResult asyncResult)
        {
            Socket s = (Socket)asyncResult.AsyncState;

            try
            {
                int byteCount = s.EndReceive(asyncResult);

                if (byteCount > 0)
                {
                    m_NextCheckActivity = Core.TickCount + 90000;

                    byte[] buffer = m_RecvBuffer;

                    if (m_Encoder != null)
                        m_Encoder.DecodeIncomingPacket(this, ref buffer, ref byteCount);

                    lock (m_Buffer)
                        m_Buffer.Enqueue(buffer, 0, byteCount);

                    m_MessagePump.OnReceive(this);

                    lock (m_AsyncLock)
                    {
                        m_AsyncState &= ~AsyncState.Pending;

                        if ((m_AsyncState & AsyncState.Paused) == 0)
                        {
                            try
                            {
                                InternalBeginReceive();
                            }
                            catch (Exception ex)
                            {
                                TraceException(ex);
                                Dispose(false);
                            }
                        }
                    }
                }
                else
                {
                    Dispose(false);
                }
            }
            catch
            {
                Dispose(false);
            }
        }

        private void OnSend(IAsyncResult asyncResult)
        {
            Socket s = (Socket)asyncResult.AsyncState;

            try
            {
                int bytes = s.EndSend(asyncResult);

                if (bytes <= 0)
                {
                    Dispose(false);
                    return;
                }

                m_NextCheckActivity = Core.TickCount + 90000;

                if (m_CoalesceSleep >= 0)
                {
                    Thread.Sleep(m_CoalesceSleep);
                }

                SendQueue.Gram gram;

                lock (m_SendQueue)
                {
                    gram = m_SendQueue.Dequeue();

                    if (gram == null && m_SendQueue.IsFlushReady)
                        gram = m_SendQueue.CheckFlushReady();
                }

                if (gram != null)
                {
                    try
                    {
                        s.BeginSend(gram.Buffer, 0, gram.Length, SocketFlags.None, m_OnSend, s);
                    }
                    catch (Exception ex)
                    {
                        TraceException(ex);
                        Dispose(false);
                    }
                }
                else
                {
                    lock (_sendL)
                        _sending = false;
                }
            }
            catch (Exception)
            {
                Dispose(false);
            }
        }

        public static void Pause()
        {
            m_Paused = true;

            for (int i = 0; i < m_Instances.Count; ++i)
            {
                NetState ns = m_Instances[i];

                lock (ns.m_AsyncLock)
                {
                    ns.m_AsyncState |= AsyncState.Paused;
                }
            }
        }

        public static void Resume()
        {
            m_Paused = false;

            for (int i = 0; i < m_Instances.Count; ++i)
            {
                NetState ns = m_Instances[i];

                if (ns.m_Socket == null)
                {
                    continue;
                }

                lock (ns.m_AsyncLock)
                {
                    ns.m_AsyncState &= ~AsyncState.Paused;

                    try
                    {
                        if ((ns.m_AsyncState & AsyncState.Pending) == 0)
                            ns.InternalBeginReceive();
                    }
                    catch (Exception ex)
                    {
                        TraceException(ex);
                        ns.Dispose(false);
                    }
                }
            }
        }

        public bool Flush()
        {
            if (m_Socket == null)
                return false;

            lock (_sendL)
            {
                if (_sending)
                    return false;

                SendQueue.Gram gram;

                lock (m_SendQueue)
                {
                    if (!m_SendQueue.IsFlushReady)
                        return false;

                    gram = m_SendQueue.CheckFlushReady();
                }

                if (gram != null)
                {
                    try
                    {
                        _sending = true;
                        m_Socket.BeginSend(gram.Buffer, 0, gram.Length, SocketFlags.None, m_OnSend, m_Socket);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TraceException(ex);
                        Dispose(false);
                    }
                }
            }

            return false;
        }
#endif

        public PacketHandler GetHandler(int packetID)
        {
            if (ContainerGridLines)
                return PacketHandlers.Get6017Handler(packetID);
            else
                return PacketHandlers.GetHandler(packetID);
        }

        public static void FlushAll()
        {
            if (m_Instances.Count >= 1024)
                Parallel.ForEach(m_Instances, ns => ns.Flush());
            else
                for (int i = 0; i < m_Instances.Count; ++i)
                {
                    m_Instances[i].Flush();
                }
        }

        private static int m_CoalesceSleep = -1;

        public static int CoalesceSleep
        {
            get
            {
                return m_CoalesceSleep;
            }
            set
            {
                m_CoalesceSleep = value;
            }
        }

        private long m_NextCheckActivity;

        public void CheckAlive(long curTicks)
        {
            if (m_Socket == null)
                return;

            if (m_NextCheckActivity - curTicks >= 0)
            {
                return;
            }

            Console.WriteLine("Client: {0}: Disconnecting due to inactivity...", this);

            Dispose();
            return;
        }

        public static void TraceException(Exception ex)
        {
            if (!Core.Debug)
                return;

            try
            {
                using (StreamWriter op = new StreamWriter("network-errors.log", true))
                {
                    op.WriteLine("# {0}", DateTime.UtcNow);

                    op.WriteLine(ex);

                    op.WriteLine();
                    op.WriteLine();
                }
            }
            catch
            {
            }

            try
            {
                Console.WriteLine(ex);
            }
            catch
            {
            }
        }

        private bool m_Disposing;

        public bool IsDisposing { get { return m_Disposing; } }

        public void Dispose()
        {
            Dispose(true);
        }

        public virtual void Dispose(bool flush)
        {
            if (m_Socket == null || m_Disposing)
            {
                return;
            }

            m_Disposing = true;

            if (flush)
                flush = Flush();

            try
            {
                m_Socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException ex)
            {
                TraceException(ex);
            }

            try
            {
                m_Socket.Close();
            }
            catch (SocketException ex)
            {
                TraceException(ex);
            }

            if (m_RecvBuffer != null)
            {
                lock (m_ReceiveBufferPool)
                    m_ReceiveBufferPool.ReleaseBuffer(m_RecvBuffer);
            }

            m_Socket = null;

            m_Buffer = null;
            m_RecvBuffer = null;

#if NewAsyncSockets
			m_ReceiveEventArgs = null;
			m_SendEventArgs = null;
#else
            m_OnReceive = null;
            m_OnSend = null;
#endif

            m_Running = false;

            lock (m_Disposed)
                m_Disposed.Enqueue(this);

            lock (m_SendQueue)
                if ( /*!flush &&*/ !m_SendQueue.IsEmpty)
                {
                    m_SendQueue.Clear();
                }
        }

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromMinutes(1.0), TimeSpan.FromMinutes(1.5), new TimerCallback(CheckAllAlive));
        }

        public static void CheckAllAlive()
        {
            try
            {
                long curTicks = Core.TickCount;

                if (m_Instances.Count >= 1024)
                    Parallel.ForEach(m_Instances, ns => ns.CheckAlive(curTicks));
                else
                    for (int i = 0; i < m_Instances.Count; ++i)
                    {
                        m_Instances[i].CheckAlive(curTicks);
                    }
            }
            catch (Exception ex)
            {
                TraceException(ex);
            }
        }

        private static Queue<NetState> m_Disposed = new Queue<NetState>();

        public static void ProcessDisposedQueue()
        {
            lock (m_Disposed)
            {
                int breakout = 0;

                while (breakout < 200 && m_Disposed.Count > 0)
                {
                    ++breakout;
                    NetState ns = m_Disposed.Dequeue();

                    Mobile m = ns.m_Mobile;
                    IAccount a = ns.m_Account;

                    if (m != null)
                    {
                        m.NetState = null;
                        ns.m_Mobile = null;
                    }

                    ns.m_Gumps.Clear();
                    ns.m_Menus.Clear();
                    ns.m_HuePickers.Clear();
                    ns.m_Account = null;
                    ns.m_ServerInfo = null;
                    ns.m_CityInfo = null;

                    m_Instances.Remove(ns);

                    if (a != null)
                    {
                        ns.WriteConsole("Disconnected. [{0} Online] [{1}]", m_Instances.Count, a);
                    }
                    else
                    {
                        ns.WriteConsole("Disconnected. [{0} Online]", m_Instances.Count);
                    }
                }
            }
        }

        public bool Running
        {
            get
            {
                return m_Running;
            }
        }

        public bool Seeded
        {
            get
            {
                return m_Seeded;
            }
            set
            {
                m_Seeded = value;
            }
        }

        public Socket Socket
        {
            get
            {
                return m_Socket;
            }
        }

        public ByteQueue Buffer
        {
            get
            {
                return m_Buffer;
            }
        }

        public ExpansionInfo ExpansionInfo
        {
            get
            {
                for (int i = ExpansionInfo.Table.Length - 1; i >= 0; i--)
                {
                    ExpansionInfo info = ExpansionInfo.Table[i];

                    if ((info.RequiredClient != null && this.Version >= info.RequiredClient) || ((this.Flags & info.ClientFlags) != 0))
                    {
                        return info;
                    }
                }

                return ExpansionInfo.GetInfo(Expansion.None);
            }
        }

        public Expansion Expansion
        {
            get
            {
                return (Expansion)this.ExpansionInfo.ID;
            }
        }

        public bool SupportsExpansion(ExpansionInfo info, bool checkCoreExpansion)
        {
            if (info == null || (checkCoreExpansion && (int)Core.Expansion < info.ID))
                return false;

            if (info.RequiredClient != null)
                return (this.Version >= info.RequiredClient);

            return ((this.Flags & info.ClientFlags) != 0);
        }

        public bool SupportsExpansion(Expansion ex, bool checkCoreExpansion)
        {
            return SupportsExpansion(ExpansionInfo.GetInfo(ex), checkCoreExpansion);
        }

        public bool SupportsExpansion(Expansion ex)
        {
            return SupportsExpansion(ex, true);
        }

        public bool SupportsExpansion(ExpansionInfo info)
        {
            return SupportsExpansion(info, true);
        }

        public int CompareTo(NetState other)
        {
            if (other == null)
                return 1;

            return m_ToString.CompareTo(other.m_ToString);
        }
    }

    public delegate void OnPacketReceive(NetState state, PacketReader pvSrc);

    public delegate bool ThrottlePacketCallback(NetState state);

    public class PacketHandler
    {
        private int m_PacketID;
        private int m_Length;
        private bool m_Ingame;
        private OnPacketReceive m_OnReceive;
        private ThrottlePacketCallback m_ThrottleCallback;

        public PacketHandler(int packetID, int length, bool ingame, OnPacketReceive onReceive)
        {
            m_PacketID = packetID;
            m_Length = length;
            m_Ingame = ingame;
            m_OnReceive = onReceive;
        }

        public int PacketID
        {
            get
            {
                return m_PacketID;
            }
        }

        public int Length
        {
            get
            {
                return m_Length;
            }
        }

        public OnPacketReceive OnReceive
        {
            get
            {
                return m_OnReceive;
            }
        }

        public ThrottlePacketCallback ThrottleCallback
        {
            get { return m_ThrottleCallback; }
            set { m_ThrottleCallback = value; }
        }

        public bool Ingame
        {
            get
            {
                return m_Ingame;
            }
        }
    }

    public enum MessageType
    {
        Regular = 0x00,
        System = 0x01,
        Emote = 0x02,
        Label = 0x06,
        Focus = 0x07,
        Whisper = 0x08,
        Yell = 0x09,
        Spell = 0x0A,

        Guild = 0x0D,
        Alliance = 0x0E,
        Command = 0x0F,

        Encoded = 0xC0
    }

    public static class PacketHandlers
    {
        private static PacketHandler[] m_Handlers;
        private static PacketHandler[] m_6017Handlers;

        private static PacketHandler[] m_ExtendedHandlersLow;
        private static Dictionary<int, PacketHandler> m_ExtendedHandlersHigh;

        private static EncodedPacketHandler[] m_EncodedHandlersLow;
        private static Dictionary<int, EncodedPacketHandler> m_EncodedHandlersHigh;

        public static PacketHandler[] Handlers
        {
            get { return m_Handlers; }
        }

        static PacketHandlers()
        {
            m_Handlers = new PacketHandler[0x100];
            m_6017Handlers = new PacketHandler[0x100];

            m_ExtendedHandlersLow = new PacketHandler[0x100];
            m_ExtendedHandlersHigh = new Dictionary<int, PacketHandler>();

            m_EncodedHandlersLow = new EncodedPacketHandler[0x100];
            m_EncodedHandlersHigh = new Dictionary<int, EncodedPacketHandler>();

            Register(0x00, 104, false, new OnPacketReceive(CreateCharacter));
            Register(0x01, 5, false, new OnPacketReceive(Disconnect));
            Register(0x02, 7, true, new OnPacketReceive(MovementReq));
            Register(0x03, 0, true, new OnPacketReceive(AsciiSpeech));
            Register(0x04, 2, true, new OnPacketReceive(GodModeRequest));
            Register(0x05, 5, true, new OnPacketReceive(AttackReq));
            Register(0x06, 5, true, new OnPacketReceive(UseReq));
            Register(0x07, 7, true, new OnPacketReceive(LiftReq));
            Register(0x08, 14, true, new OnPacketReceive(DropReq));
            Register(0x09, 5, true, new OnPacketReceive(LookReq));
            Register(0x0A, 11, true, new OnPacketReceive(Edit));
            Register(0x12, 0, true, new OnPacketReceive(TextCommand));
            Register(0x13, 10, true, new OnPacketReceive(EquipReq));
            Register(0x14, 6, true, new OnPacketReceive(ChangeZ));
            Register(0x22, 3, true, new OnPacketReceive(Resynchronize));
            Register(0x2C, 2, true, new OnPacketReceive(DeathStatusResponse));
            Register(0x34, 10, true, new OnPacketReceive(MobileQuery));
            Register(0x3A, 0, true, new OnPacketReceive(ChangeSkillLock));
            Register(0x3B, 0, true, new OnPacketReceive(VendorBuyReply));
            Register(0x47, 11, true, new OnPacketReceive(NewTerrain));
            Register(0x48, 73, true, new OnPacketReceive(NewAnimData));
            Register(0x58, 106, true, new OnPacketReceive(NewRegion));
            Register(0x5D, 73, false, new OnPacketReceive(PlayCharacter));
            Register(0x61, 9, true, new OnPacketReceive(DeleteStatic));
            Register(0x6C, 19, true, new OnPacketReceive(TargetResponse));
            Register(0x6F, 0, true, new OnPacketReceive(SecureTrade));
            Register(0x72, 5, true, new OnPacketReceive(SetWarMode));
            Register(0x73, 2, false, new OnPacketReceive(PingReq));
            Register(0x75, 35, true, new OnPacketReceive(RenameRequest));
            Register(0x79, 9, true, new OnPacketReceive(ResourceQuery));
            Register(0x7E, 2, true, new OnPacketReceive(GodviewQuery));
            Register(0x7D, 13, true, new OnPacketReceive(MenuResponse));
            Register(0x80, 62, false, new OnPacketReceive(AccountLogin));
            Register(0x83, 39, false, new OnPacketReceive(DeleteCharacter));
            Register(0x91, 65, false, new OnPacketReceive(GameLogin));
            Register(0x95, 9, true, new OnPacketReceive(HuePickerResponse));
            Register(0x96, 0, true, new OnPacketReceive(GameCentralMoniter));
            Register(0x98, 0, true, new OnPacketReceive(MobileNameRequest));
            Register(0x9A, 0, true, new OnPacketReceive(AsciiPromptResponse));
            Register(0x9B, 258, true, new OnPacketReceive(HelpRequest));
            Register(0x9D, 51, true, new OnPacketReceive(GMSingle));
            Register(0x9F, 0, true, new OnPacketReceive(VendorSellReply));
            Register(0xA0, 3, false, new OnPacketReceive(PlayServer));
            Register(0xA4, 149, false, new OnPacketReceive(SystemInfo));
            Register(0xA7, 4, true, new OnPacketReceive(RequestScrollWindow));
            Register(0xAD, 0, true, new OnPacketReceive(UnicodeSpeech));
            Register(0xB1, 0, true, new OnPacketReceive(DisplayGumpResponse));
            Register(0xB5, 64, true, new OnPacketReceive(ChatRequest));
            Register(0xB6, 9, true, new OnPacketReceive(ObjectHelpRequest));
            Register(0xB8, 0, true, new OnPacketReceive(ProfileReq));
            Register(0xBB, 9, false, new OnPacketReceive(AccountID));
            Register(0xBD, 0, false, new OnPacketReceive(ClientVersion));
            Register(0xBE, 0, true, new OnPacketReceive(AssistVersion));
            Register(0xBF, 0, true, new OnPacketReceive(ExtendedCommand));
            Register(0xC2, 0, true, new OnPacketReceive(UnicodePromptResponse));
            Register(0xC8, 2, true, new OnPacketReceive(SetUpdateRange));
            Register(0xC9, 6, true, new OnPacketReceive(TripTime));
            Register(0xCA, 6, true, new OnPacketReceive(UTripTime));
            Register(0xCF, 0, false, new OnPacketReceive(AccountLogin));
            Register(0xD0, 0, true, new OnPacketReceive(ConfigurationFile));
            Register(0xD1, 2, true, new OnPacketReceive(LogoutReq));
            Register(0xD6, 0, true, new OnPacketReceive(BatchQueryProperties));
            Register(0xD7, 0, true, new OnPacketReceive(EncodedCommand));
            Register(0xE1, 0, false, new OnPacketReceive(ClientType));
            Register(0xEF, 21, false, new OnPacketReceive(LoginServerSeed));
            Register(0xF4, 0, false, new OnPacketReceive(CrashReport));
            Register(0xF8, 106, false, new OnPacketReceive(CreateCharacter70160));

            Register6017(0x08, 15, true, new OnPacketReceive(DropReq6017));

            RegisterExtended(0x05, false, new OnPacketReceive(ScreenSize));
            RegisterExtended(0x06, true, new OnPacketReceive(PartyMessage));
            RegisterExtended(0x07, true, new OnPacketReceive(QuestArrow));
            RegisterExtended(0x09, true, new OnPacketReceive(DisarmRequest));
            RegisterExtended(0x0A, true, new OnPacketReceive(StunRequest));
            RegisterExtended(0x0B, false, new OnPacketReceive(Language));
            RegisterExtended(0x0C, true, new OnPacketReceive(CloseStatus));
            RegisterExtended(0x0E, true, new OnPacketReceive(Animate));
            RegisterExtended(0x0F, false, new OnPacketReceive(Empty)); // What's this?
            RegisterExtended(0x10, true, new OnPacketReceive(QueryProperties));
            RegisterExtended(0x13, true, new OnPacketReceive(ContextMenuRequest));
            RegisterExtended(0x15, true, new OnPacketReceive(ContextMenuResponse));
            RegisterExtended(0x1A, true, new OnPacketReceive(StatLockChange));
            RegisterExtended(0x1C, true, new OnPacketReceive(CastSpell));
            RegisterExtended(0x24, false, new OnPacketReceive(UnhandledBF));
            RegisterExtended(0x2C, true, new OnPacketReceive(BandageTarget));
            RegisterExtended(0x32, true, new OnPacketReceive(ToggleFlying));

            RegisterEncoded(0x19, true, new OnEncodedPacketReceive(SetAbility));
            RegisterEncoded(0x28, true, new OnEncodedPacketReceive(GuildGumpRequest));

            RegisterEncoded(0x32, true, new OnEncodedPacketReceive(QuestGumpRequest));
        }

        public static void Register(int packetID, int length, bool ingame, OnPacketReceive onReceive)
        {
            m_Handlers[packetID] = new PacketHandler(packetID, length, ingame, onReceive);

            if (m_6017Handlers[packetID] == null)
                m_6017Handlers[packetID] = new PacketHandler(packetID, length, ingame, onReceive);
        }

        public static PacketHandler GetHandler(int packetID)
        {
            return m_Handlers[packetID];
        }

        public static void Register6017(int packetID, int length, bool ingame, OnPacketReceive onReceive)
        {
            m_6017Handlers[packetID] = new PacketHandler(packetID, length, ingame, onReceive);
        }

        public static PacketHandler Get6017Handler(int packetID)
        {
            return m_6017Handlers[packetID];
        }

        public static void RegisterExtended(int packetID, bool ingame, OnPacketReceive onReceive)
        {
            if (packetID >= 0 && packetID < 0x100)
                m_ExtendedHandlersLow[packetID] = new PacketHandler(packetID, 0, ingame, onReceive);
            else
                m_ExtendedHandlersHigh[packetID] = new PacketHandler(packetID, 0, ingame, onReceive);
        }

        public static PacketHandler GetExtendedHandler(int packetID)
        {
            if (packetID >= 0 && packetID < 0x100)
                return m_ExtendedHandlersLow[packetID];
            else
            {
                PacketHandler handler;
                m_ExtendedHandlersHigh.TryGetValue(packetID, out handler);
                return handler;
            }
        }

        public static void RemoveExtendedHandler(int packetID)
        {
            if (packetID >= 0 && packetID < 0x100)
                m_ExtendedHandlersLow[packetID] = null;
            else
                m_ExtendedHandlersHigh.Remove(packetID);
        }

        public static void RegisterEncoded(int packetID, bool ingame, OnEncodedPacketReceive onReceive)
        {
            if (packetID >= 0 && packetID < 0x100)
                m_EncodedHandlersLow[packetID] = new EncodedPacketHandler(packetID, ingame, onReceive);
            else
                m_EncodedHandlersHigh[packetID] = new EncodedPacketHandler(packetID, ingame, onReceive);
        }

        public static EncodedPacketHandler GetEncodedHandler(int packetID)
        {
            if (packetID >= 0 && packetID < 0x100)
                return m_EncodedHandlersLow[packetID];
            else
            {
                EncodedPacketHandler handler;
                m_EncodedHandlersHigh.TryGetValue(packetID, out handler);
                return handler;
            }
        }

        public static void RemoveEncodedHandler(int packetID)
        {
            if (packetID >= 0 && packetID < 0x100)
                m_EncodedHandlersLow[packetID] = null;
            else
                m_EncodedHandlersHigh.Remove(packetID);
        }

        public static void RegisterThrottler(int packetID, ThrottlePacketCallback t)
        {
            PacketHandler ph = GetHandler(packetID);

            if (ph != null)
                ph.ThrottleCallback = t;

            ph = Get6017Handler(packetID);

            if (ph != null)
                ph.ThrottleCallback = t;
        }

        private static void UnhandledBF(NetState state, PacketReader pvSrc)
        {
        }

        public static void Empty(NetState state, PacketReader pvSrc)
        {
        }

        public static void SetAbility(NetState state, IEntity e, EncodedReader reader)
        {
            EventSink.InvokeSetAbility(new SetAbilityEventArgs(state.Mobile, reader.ReadInt32()));
        }

        public static void GuildGumpRequest(NetState state, IEntity e, EncodedReader reader)
        {
            EventSink.InvokeGuildGumpRequest(new GuildGumpRequestArgs(state.Mobile));
        }

        public static void QuestGumpRequest(NetState state, IEntity e, EncodedReader reader)
        {
            EventSink.InvokeQuestGumpRequest(new QuestGumpRequestArgs(state.Mobile));
        }

        public static void EncodedCommand(NetState state, PacketReader pvSrc)
        {
            IEntity e = World.FindEntity(pvSrc.ReadInt32());
            int packetID = pvSrc.ReadUInt16();

            EncodedPacketHandler ph = GetEncodedHandler(packetID);

            if (ph != null)
            {
                if (ph.Ingame && state.Mobile == null)
                {
                    Console.WriteLine("Client: {0}: Sent ingame packet (0xD7x{1:X2}) before having been attached to a mobile", state, packetID);
                    state.Dispose();
                }
                else if (ph.Ingame && state.Mobile.Deleted)
                {
                    state.Dispose();
                }
                else
                {
                    ph.OnReceive(state, e, new EncodedReader(pvSrc));
                }
            }
            else
            {
                pvSrc.Trace(state);
            }
        }

        public static void RenameRequest(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;
            Mobile targ = World.FindMobile(pvSrc.ReadInt32());

            if (targ != null)
                EventSink.InvokeRenameRequest(new RenameRequestEventArgs(from, targ, pvSrc.ReadStringSafe()));
        }

        public static void ChatRequest(NetState state, PacketReader pvSrc)
        {
            EventSink.InvokeChatRequest(new ChatRequestEventArgs(state.Mobile));
        }

        public static void SecureTrade(NetState state, PacketReader pvSrc)
        {
            switch (pvSrc.ReadByte())
            {
                case 1: // Cancel
                    {
                        Serial serial = pvSrc.ReadInt32();

                        SecureTradeContainer cont = World.FindItem(serial) as SecureTradeContainer;

                        if (cont != null && cont.Trade != null && (cont.Trade.From.Mobile == state.Mobile || cont.Trade.To.Mobile == state.Mobile))
                            cont.Trade.Cancel();

                        break;
                    }
                case 2: // Check
                    {
                        Serial serial = pvSrc.ReadInt32();

                        SecureTradeContainer cont = World.FindItem(serial) as SecureTradeContainer;

                        if (cont != null)
                        {
                            SecureTrade trade = cont.Trade;

                            bool value = (pvSrc.ReadInt32() != 0);

                            if (trade != null && trade.From.Mobile == state.Mobile)
                            {
                                trade.From.Accepted = value;
                                trade.Update();
                            }
                            else if (trade != null && trade.To.Mobile == state.Mobile)
                            {
                                trade.To.Accepted = value;
                                trade.Update();
                            }
                        }

                        break;
                    }
                case 3: // Update Gold
                    {
                        Serial serial = pvSrc.ReadInt32();

                        SecureTradeContainer cont = World.FindItem(serial) as SecureTradeContainer;

                        if (cont != null)
                        {
                            int gold = pvSrc.ReadInt32();
                            int plat = pvSrc.ReadInt32();

                            SecureTrade trade = cont.Trade;

                            if (trade != null)
                            {
                                if (trade.From.Mobile == state.Mobile)
                                {
                                    trade.From.Gold = gold;
                                    trade.From.Plat = plat;
                                    trade.UpdateFromCurrency();
                                }
                                else if (trade.To.Mobile == state.Mobile)
                                {
                                    trade.To.Gold = gold;
                                    trade.To.Plat = plat;
                                    trade.UpdateToCurrency();
                                }
                            }
                        }
                    }
                    break;
            }
        }

        public static void VendorBuyReply(NetState state, PacketReader pvSrc)
        {
            pvSrc.Seek(1, SeekOrigin.Begin);

            int msgSize = pvSrc.ReadUInt16();
            Mobile vendor = World.FindMobile(pvSrc.ReadInt32());
            byte flag = pvSrc.ReadByte();

            if (vendor == null)
            {
                return;
            }
            else if (vendor.Deleted || !Utility.RangeCheck(vendor.Location, state.Mobile.Location, 10))
            {
                state.Send(new EndVendorBuy(vendor));
                return;
            }

            if (flag == 0x02)
            {
                msgSize -= 1 + 2 + 4 + 1;

                if ((msgSize / 7) > 100)
                    return;

                List<BuyItemResponse> buyList = new List<BuyItemResponse>(msgSize / 7);
                for (; msgSize > 0; msgSize -= 7)
                {
                    byte layer = pvSrc.ReadByte();
                    Serial serial = pvSrc.ReadInt32();
                    int amount = pvSrc.ReadInt16();

                    buyList.Add(new BuyItemResponse(serial, amount));
                }

                if (buyList.Count > 0)
                {
                    IVendor v = vendor as IVendor;

                    if (v != null && v.OnBuyItems(state.Mobile, buyList))
                        state.Send(new EndVendorBuy(vendor));
                }
            }
            else
            {
                state.Send(new EndVendorBuy(vendor));
            }
        }

        public static void VendorSellReply(NetState state, PacketReader pvSrc)
        {
            Serial serial = pvSrc.ReadInt32();
            Mobile vendor = World.FindMobile(serial);

            if (vendor == null)
            {
                return;
            }
            else if (vendor.Deleted || !Utility.RangeCheck(vendor.Location, state.Mobile.Location, 10))
            {
                state.Send(new EndVendorSell(vendor));
                return;
            }

            int count = pvSrc.ReadUInt16();
            if (count < 100 && pvSrc.Size == (1 + 2 + 4 + 2 + (count * 6)))
            {
                List<SellItemResponse> sellList = new List<SellItemResponse>(count);

                for (int i = 0; i < count; i++)
                {
                    Item item = World.FindItem(pvSrc.ReadInt32());
                    int Amount = pvSrc.ReadInt16();

                    if (item != null && Amount > 0)
                        sellList.Add(new SellItemResponse(item, Amount));
                }

                if (sellList.Count > 0)
                {
                    IVendor v = vendor as IVendor;

                    if (v != null && v.OnSellItems(state.Mobile, sellList))
                        state.Send(new EndVendorSell(vendor));
                }
            }
        }

        public static void DeleteCharacter(NetState state, PacketReader pvSrc)
        {
            pvSrc.Seek(30, SeekOrigin.Current);
            int index = pvSrc.ReadInt32();

            EventSink.InvokeDeleteRequest(new DeleteRequestEventArgs(state, index));
        }

        public static void ResourceQuery(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
            }
        }

        public static void GameCentralMoniter(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                int type = pvSrc.ReadByte();
                int num1 = pvSrc.ReadInt32();

                Console.WriteLine("God Client: {0}: Game central moniter", state);
                Console.WriteLine(" - Type: {0}", type);
                Console.WriteLine(" - Number: {0}", num1);

                pvSrc.Trace(state);
            }
        }

        public static void GodviewQuery(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                Console.WriteLine("God Client: {0}: Godview query 0x{1:X}", state, pvSrc.ReadByte());
            }
        }

        public static void GMSingle(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
                pvSrc.Trace(state);
        }

        public static void DeathStatusResponse(NetState state, PacketReader pvSrc)
        {
            // Ignored
        }

        public static void ObjectHelpRequest(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            Serial serial = pvSrc.ReadInt32();
            int unk = pvSrc.ReadByte();
            string lang = pvSrc.ReadString(3);

            if (serial.IsItem)
            {
                Item item = World.FindItem(serial);

                if (item != null && from.Map == item.Map && Utility.InUpdateRange(item.GetWorldLocation(), from.Location) && from.CanSee(item))
                    item.OnHelpRequest(from);
            }
            else if (serial.IsMobile)
            {
                Mobile m = World.FindMobile(serial);

                if (m != null && from.Map == m.Map && Utility.InUpdateRange(m.Location, from.Location) && from.CanSee(m))
                    m.OnHelpRequest(m);
            }
        }

        public static void MobileNameRequest(NetState state, PacketReader pvSrc)
        {
            Mobile m = World.FindMobile(pvSrc.ReadInt32());

            if (m != null && Utility.InUpdateRange(state.Mobile, m) && state.Mobile.CanSee(m))
                state.Send(new MobileName(m));
        }

        public static void RequestScrollWindow(NetState state, PacketReader pvSrc)
        {
            int lastTip = pvSrc.ReadInt16();
            int type = pvSrc.ReadByte();
        }

        public static void AttackReq(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;
            Mobile m = World.FindMobile(pvSrc.ReadInt32());

            if (m != null)
                from.Attack(m);
        }

        public static void HuePickerResponse(NetState state, PacketReader pvSrc)
        {
            int serial = pvSrc.ReadInt32();
            int value = pvSrc.ReadInt16();
            int hue = pvSrc.ReadInt16() & 0x3FFF;

            hue = Utility.ClipDyedHue(hue);

            foreach (HuePicker huePicker in state.HuePickers)
            {
                if (huePicker.Serial == serial)
                {
                    state.RemoveHuePicker(huePicker);

                    huePicker.OnResponse(hue);

                    break;
                }
            }
        }

        public static void TripTime(NetState state, PacketReader pvSrc)
        {
            int unk1 = pvSrc.ReadByte();
            int unk2 = pvSrc.ReadInt32();

            state.Send(new TripTimeResponse(unk1));
        }

        public static void UTripTime(NetState state, PacketReader pvSrc)
        {
            int unk1 = pvSrc.ReadByte();
            int unk2 = pvSrc.ReadInt32();

            state.Send(new UTripTimeResponse(unk1));
        }

        public static void ChangeZ(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                int x = pvSrc.ReadInt16();
                int y = pvSrc.ReadInt16();
                int z = pvSrc.ReadSByte();

                Console.WriteLine("God Client: {0}: Change Z ({1}, {2}, {3})", state, x, y, z);
            }
        }

        public static void SystemInfo(NetState state, PacketReader pvSrc)
        {
            int v1 = pvSrc.ReadByte();
            int v2 = pvSrc.ReadUInt16();
            int v3 = pvSrc.ReadByte();
            string s1 = pvSrc.ReadString(32);
            string s2 = pvSrc.ReadString(32);
            string s3 = pvSrc.ReadString(32);
            string s4 = pvSrc.ReadString(32);
            int v4 = pvSrc.ReadUInt16();
            int v5 = pvSrc.ReadUInt16();
            int v6 = pvSrc.ReadInt32();
            int v7 = pvSrc.ReadInt32();
            int v8 = pvSrc.ReadInt32();
        }

        public static void Edit(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                int type = pvSrc.ReadByte(); // 10 = static, 7 = npc, 4 = dynamic
                int x = pvSrc.ReadInt16();
                int y = pvSrc.ReadInt16();
                int id = pvSrc.ReadInt16();
                int z = pvSrc.ReadSByte();
                int hue = pvSrc.ReadUInt16();

                Console.WriteLine("God Client: {0}: Edit {6} ({1}, {2}, {3}) 0x{4:X} (0x{5:X})", state, x, y, z, id, hue, type);
            }
        }

        public static void DeleteStatic(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                int x = pvSrc.ReadInt16();
                int y = pvSrc.ReadInt16();
                int z = pvSrc.ReadInt16();
                int id = pvSrc.ReadUInt16();

                Console.WriteLine("God Client: {0}: Delete Static ({1}, {2}, {3}) 0x{4:X}", state, x, y, z, id);
            }
        }

        public static void NewAnimData(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                Console.WriteLine("God Client: {0}: New tile animation", state);

                pvSrc.Trace(state);
            }
        }

        public static void NewTerrain(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                int x = pvSrc.ReadInt16();
                int y = pvSrc.ReadInt16();
                int id = pvSrc.ReadUInt16();
                int width = pvSrc.ReadInt16();
                int height = pvSrc.ReadInt16();

                Console.WriteLine("God Client: {0}: New Terrain ({1}, {2})+({3}, {4}) 0x{5:X4}", state, x, y, width, height, id);
            }
        }

        public static void NewRegion(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                string name = pvSrc.ReadString(40);
                int unk = pvSrc.ReadInt32();
                int x = pvSrc.ReadInt16();
                int y = pvSrc.ReadInt16();
                int width = pvSrc.ReadInt16();
                int height = pvSrc.ReadInt16();
                int zStart = pvSrc.ReadInt16();
                int zEnd = pvSrc.ReadInt16();
                string desc = pvSrc.ReadString(40);
                int soundFX = pvSrc.ReadInt16();
                int music = pvSrc.ReadInt16();
                int nightFX = pvSrc.ReadInt16();
                int dungeon = pvSrc.ReadByte();
                int light = pvSrc.ReadInt16();

                Console.WriteLine("God Client: {0}: New Region '{1}' ('{2}')", state, name, desc);
            }
        }

        public static void AccountID(NetState state, PacketReader pvSrc)
        {
        }

        public static bool VerifyGC(NetState state)
        {
            if (state.Mobile == null || state.Mobile.AccessLevel <= AccessLevel.Counselor)
            {
                if (state.Running)
                    Console.WriteLine("Warning: {0}: Player using godclient, disconnecting", state);

                state.Dispose();
                return false;
            }
            else
            {
                return true;
            }
        }

        public static void TextCommand(NetState state, PacketReader pvSrc)
        {
            int type = pvSrc.ReadByte();
            string command = pvSrc.ReadString();

            Mobile m = state.Mobile;

            switch (type)
            {
                case 0x00: // Go
                    {
                        if (VerifyGC(state))
                        {
                            try
                            {
                                string[] split = command.Split(' ');

                                int x = Utility.ToInt32(split[0]);
                                int y = Utility.ToInt32(split[1]);

                                int z;

                                if (split.Length >= 3)
                                    z = Utility.ToInt32(split[2]);
                                else if (m.Map != null)
                                    z = m.Map.GetAverageZ(x, y);
                                else
                                    z = 0;

                                m.Location = new Point3D(x, y, z);
                            }
                            catch
                            {
                            }
                        }

                        break;
                    }
                case 0xC7: // Animate
                    {
                        EventSink.InvokeAnimateRequest(new AnimateRequestEventArgs(m, command));

                        break;
                    }
                case 0x24: // Use skill
                    {
                        int skillIndex;

                        if (!int.TryParse(command.Split(' ')[0], out skillIndex))
                            break;

                        Skills.UseSkill(m, skillIndex);

                        break;
                    }
                case 0x43: // Open spellbook
                    {
                        int booktype;

                        if (!int.TryParse(command, out booktype))
                            booktype = 1;

                        EventSink.InvokeOpenSpellbookRequest(new OpenSpellbookRequestEventArgs(m, booktype));

                        break;
                    }
                case 0x27: // Cast spell from book
                    {
                        string[] split = command.Split(' ');

                        if (split.Length > 0)
                        {
                            int spellID = Utility.ToInt32(split[0]) - 1;
                            int serial = split.Length > 1 ? Utility.ToInt32(split[1]) : -1;

                            EventSink.InvokeCastSpellRequest(new CastSpellRequestEventArgs(m, spellID, World.FindItem(serial)));
                        }

                        break;
                    }
                case 0x58: // Open door
                    {
                        EventSink.InvokeOpenDoorMacroUsed(new OpenDoorMacroEventArgs(m));

                        break;
                    }
                case 0x56: // Cast spell from macro
                    {
                        int spellID = Utility.ToInt32(command) - 1;

                        EventSink.InvokeCastSpellRequest(new CastSpellRequestEventArgs(m, spellID, null));

                        break;
                    }
                case 0xF4: // Invoke virtues from macro
                    {
                        int virtueID = Utility.ToInt32(command) - 1;

                        EventSink.InvokeVirtueMacroRequest(new VirtueMacroRequestEventArgs(m, virtueID));

                        break;
                    }
                case 0x2F: // Old scroll double click
                    {
                        /*
                         * This command is still sent for items 0xEF3 - 0xEF9
                         *
                         * Command is one of three, depending on the item ID of the scroll:
                         * - [scroll serial]
                         * - [scroll serial] [target serial]
                         * - [scroll serial] [x] [y] [z]
                         */
                        break;
                    }
                default:
                    {
                        Console.WriteLine("Client: {0}: Unknown text-command type 0x{1:X2}: {2}", state, type, command);
                        break;
                    }
            }
        }

        public static void GodModeRequest(NetState state, PacketReader pvSrc)
        {
            if (VerifyGC(state))
            {
                state.Send(new GodModeReply(pvSrc.ReadBoolean()));
            }
        }

        public static void AsciiPromptResponse(NetState state, PacketReader pvSrc)
        {
            int serial = pvSrc.ReadInt32();
            int prompt = pvSrc.ReadInt32();
            int type = pvSrc.ReadInt32();
            string text = pvSrc.ReadStringSafe();

            if (text.Length > 128)
                return;

            Mobile from = state.Mobile;
            Prompt p = from.Prompt;

            if (p != null && p.Serial == serial && p.Serial == prompt)
            {
                from.Prompt = null;

                if (type == 0)
                    p.OnCancel(from);
                else
                    p.OnResponse(from, text);
            }
        }

        public static void UnicodePromptResponse(NetState state, PacketReader pvSrc)
        {
            int serial = pvSrc.ReadInt32();
            int prompt = pvSrc.ReadInt32();
            int type = pvSrc.ReadInt32();
            string lang = pvSrc.ReadString(4);
            string text = pvSrc.ReadUnicodeStringLESafe();

            if (text.Length > 128)
                return;

            Mobile from = state.Mobile;
            Prompt p = from.Prompt;

            if (p != null && p.Serial == serial && p.Serial == prompt)
            {
                from.Prompt = null;

                if (type == 0)
                    p.OnCancel(from);
                else
                    p.OnResponse(from, text);
            }
        }

        public static void MenuResponse(NetState state, PacketReader pvSrc)
        {
            int serial = pvSrc.ReadInt32();
            int menuID = pvSrc.ReadInt16(); // unused in our implementation
            int index = pvSrc.ReadInt16();
            int itemID = pvSrc.ReadInt16();
            int hue = pvSrc.ReadInt16();

            index -= 1; // convert from 1-based to 0-based

            foreach (IMenu menu in state.Menus)
            {
                if (menu.Serial == serial)
                {
                    state.RemoveMenu(menu);

                    if (index >= 0 && index < menu.EntryLength)
                    {
                        menu.OnResponse(state, index);
                    }
                    else
                    {
                        menu.OnCancel(state);
                    }

                    break;
                }
            }
        }

        public static void ProfileReq(NetState state, PacketReader pvSrc)
        {
            int type = pvSrc.ReadByte();
            Serial serial = pvSrc.ReadInt32();

            Mobile beholder = state.Mobile;
            Mobile beheld = World.FindMobile(serial);

            if (beheld == null)
                return;

            switch (type)
            {
                case 0x00: // display request
                    {
                        EventSink.InvokeProfileRequest(new ProfileRequestEventArgs(beholder, beheld));

                        break;
                    }
                case 0x01: // edit request
                    {
                        pvSrc.ReadInt16(); // Skip
                        int length = pvSrc.ReadUInt16();

                        if (length > 511)
                            return;

                        string text = pvSrc.ReadUnicodeString(length);

                        EventSink.InvokeChangeProfileRequest(new ChangeProfileRequestEventArgs(beholder, beheld, text));

                        break;
                    }
            }
        }

        public static void Disconnect(NetState state, PacketReader pvSrc)
        {
            int minusOne = pvSrc.ReadInt32();
        }

        public static void LiftReq(NetState state, PacketReader pvSrc)
        {
            Serial serial = pvSrc.ReadInt32();
            int amount = pvSrc.ReadUInt16();
            Item item = World.FindItem(serial);

            bool rejected;
            LRReason reject;

            state.Mobile.Lift(item, amount, out rejected, out reject);
        }

        public static void EquipReq(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;
            Item item = from.Holding;

            bool valid = (item != null && item.HeldBy == from && item.Map == Map.Internal);

            from.Holding = null;

            if (!valid)
            {
                return;
            }

            pvSrc.Seek(5, SeekOrigin.Current);
            Mobile to = World.FindMobile(pvSrc.ReadInt32());

            if (to == null)
                to = from;

            if (!to.AllowEquipFrom(from) || !to.EquipItem(item))
                item.Bounce(from);

            item.ClearBounce();
        }

        public static void DropReq(NetState state, PacketReader pvSrc)
        {
            pvSrc.ReadInt32(); // serial, ignored
            int x = pvSrc.ReadInt16();
            int y = pvSrc.ReadInt16();
            int z = pvSrc.ReadSByte();
            Serial dest = pvSrc.ReadInt32();

            Point3D loc = new Point3D(x, y, z);

            Mobile from = state.Mobile;

            if (dest.IsMobile)
            {
                from.Drop(World.FindMobile(dest), loc);
            }
            else if (dest.IsItem)
            {
                Item item = World.FindItem(dest);

                if (item is BaseMulti && ((BaseMulti)item).AllowsRelativeDrop)
                {
                    loc.m_X += item.X;
                    loc.m_Y += item.Y;
                    from.Drop(loc);
                }
                else
                {
                    from.Drop(item, loc);
                }
            }
            else
            {
                from.Drop(loc);
            }
        }

        public static void DropReq6017(NetState state, PacketReader pvSrc)
        {
            pvSrc.ReadInt32(); // serial, ignored
            int x = pvSrc.ReadInt16();
            int y = pvSrc.ReadInt16();
            int z = pvSrc.ReadSByte();
            pvSrc.ReadByte(); // Grid Location?
            Serial dest = pvSrc.ReadInt32();

            Point3D loc = new Point3D(x, y, z);

            Mobile from = state.Mobile;

            if (dest.IsMobile)
            {
                from.Drop(World.FindMobile(dest), loc);
            }
            else if (dest.IsItem)
            {
                Item item = World.FindItem(dest);

                if (item is BaseMulti && ((BaseMulti)item).AllowsRelativeDrop)
                {
                    loc.m_X += item.X;
                    loc.m_Y += item.Y;
                    from.Drop(loc);
                }
                else
                {
                    from.Drop(item, loc);
                }
            }
            else
            {
                from.Drop(loc);
            }
        }

        public static void ConfigurationFile(NetState state, PacketReader pvSrc)
        {
        }

        public static void LogoutReq(NetState state, PacketReader pvSrc)
        {
            state.Send(new LogoutAck());
        }

        public static void ChangeSkillLock(NetState state, PacketReader pvSrc)
        {
            Skill s = state.Mobile.Skills[pvSrc.ReadInt16()];

            if (s != null)
                s.SetLockNoRelay((SkillLock)pvSrc.ReadByte());
        }

        public static void HelpRequest(NetState state, PacketReader pvSrc)
        {
            EventSink.InvokeHelpRequest(new HelpRequestEventArgs(state.Mobile));
        }

        public static void TargetResponse(NetState state, PacketReader pvSrc)
        {
            int type = pvSrc.ReadByte();
            int targetID = pvSrc.ReadInt32();
            int flags = pvSrc.ReadByte();
            Serial serial = pvSrc.ReadInt32();
            int x = pvSrc.ReadInt16(), y = pvSrc.ReadInt16(), z = pvSrc.ReadInt16();
            int graphic = pvSrc.ReadUInt16();

            if (targetID == unchecked((int)0xDEADBEEF))
                return;

            Mobile from = state.Mobile;

            Target t = from.Target;

            if (t != null)
            {
                TargetProfile prof = TargetProfile.Acquire(t.GetType());

                if (prof != null)
                {
                    prof.Start();
                }

                try
                {
                    if (x == -1 && y == -1 && !serial.IsValid)
                    {
                        // User pressed escape
                        t.Cancel(from, TargetCancelType.Canceled);
                    }
                    else if (Target.TargetIDValidation && t.TargetID != targetID)
                    {
                        // Sanity, prevent fake target
                        return;
                    }
                    else
                    {
                        object toTarget;

                        if (type == 1)
                        {
                            if (graphic == 0)
                            {
                                toTarget = new LandTarget(new Point3D(x, y, z), from.Map);
                            }
                            else
                            {
                                Map map = from.Map;

                                if (map == null || map == Map.Internal)
                                {
                                    t.Cancel(from, TargetCancelType.Canceled);
                                    return;
                                }
                                else
                                {
                                    StaticTile[] tiles = map.Tiles.GetStaticTiles(x, y, !t.DisallowMultis);

                                    bool valid = false;

                                    if (state.HighSeas)
                                    {
                                        ItemData id = TileData.ItemTable[graphic & TileData.MaxItemValue];
                                        if (id.Surface)
                                        {
                                            z -= id.Height;
                                        }
                                    }

                                    for (int i = 0; !valid && i < tiles.Length; ++i)
                                    {
                                        if (tiles[i].Z == z && tiles[i].ID == graphic)
                                            valid = true;
                                    }

                                    if (!valid)
                                    {
                                        t.Cancel(from, TargetCancelType.Canceled);
                                        return;
                                    }
                                    else
                                    {
                                        toTarget = new StaticTarget(new Point3D(x, y, z), graphic);
                                    }
                                }
                            }
                        }
                        else if (serial.IsMobile)
                        {
                            toTarget = World.FindMobile(serial);
                        }
                        else if (serial.IsItem)
                        {
                            toTarget = World.FindItem(serial);
                        }
                        else
                        {
                            t.Cancel(from, TargetCancelType.Canceled);
                            return;
                        }

                        t.Invoke(from, toTarget);
                    }
                }
                finally
                {
                    if (prof != null)
                    {
                        prof.Finish();
                    }
                }
            }
        }

        public static void DisplayGumpResponse(NetState state, PacketReader pvSrc)
        {
            int serial = pvSrc.ReadInt32();
            int typeID = pvSrc.ReadInt32();
            int buttonID = pvSrc.ReadInt32();

            foreach (Gump gump in state.Gumps)
            {
                if (gump.Serial == serial && gump.TypeID == typeID)
                {
                    var buttonExists = buttonID == 0; // 0 is always 'close'

                    if (!buttonExists)
                    {
                        foreach (var e in gump.Entries)
                        {
                            if (e is GumpButton && ((GumpButton)e).ButtonID == buttonID)
                            {
                                buttonExists = true;
                                break;
                            }

                            if (e is GumpImageTileButton && ((GumpImageTileButton)e).ButtonID == buttonID)
                            {
                                buttonExists = true;
                                break;
                            }
                        }
                    }

                    if (!buttonExists)
                    {
                        state.WriteConsole("Invalid gump response, disconnecting...");
                        state.Dispose();
                        return;
                    }

                    int switchCount = pvSrc.ReadInt32();

                    if (switchCount < 0 || switchCount > gump.m_Switches)
                    {
                        state.WriteConsole("Invalid gump response, disconnecting...");
                        state.Dispose();
                        return;
                    }

                    int[] switches = new int[switchCount];

                    for (int j = 0; j < switches.Length; ++j)
                        switches[j] = pvSrc.ReadInt32();

                    int textCount = pvSrc.ReadInt32();

                    if (textCount < 0 || textCount > gump.m_TextEntries)
                    {
                        state.WriteConsole("Invalid gump response, disconnecting...");
                        state.Dispose();
                        return;
                    }

                    TextRelay[] textEntries = new TextRelay[textCount];

                    for (int j = 0; j < textEntries.Length; ++j)
                    {
                        int entryID = pvSrc.ReadUInt16();
                        int textLength = pvSrc.ReadUInt16();

                        if (textLength > 239)
                        {
                            state.WriteConsole("Invalid gump response, disconnecting...");
                            state.Dispose();
                            return;
                        }

                        string text = pvSrc.ReadUnicodeStringSafe(textLength);
                        textEntries[j] = new TextRelay(entryID, text);
                    }

                    state.RemoveGump(gump);

                    GumpProfile prof = GumpProfile.Acquire(gump.GetType());

                    if (prof != null)
                    {
                        prof.Start();
                    }

                    gump.OnResponse(state, new RelayInfo(buttonID, switches, textEntries));

                    if (prof != null)
                    {
                        prof.Finish();
                    }

                    return;
                }
            }

            if (typeID == 461)
            { // Virtue gump
                int switchCount = pvSrc.ReadInt32();

                if (buttonID == 1 && switchCount > 0)
                {
                    Mobile beheld = World.FindMobile(pvSrc.ReadInt32());

                    if (beheld != null)
                    {
                        EventSink.InvokeVirtueGumpRequest(new VirtueGumpRequestEventArgs(state.Mobile, beheld));
                    }
                }
                else
                {
                    Mobile beheld = World.FindMobile(serial);

                    if (beheld != null)
                    {
                        EventSink.InvokeVirtueItemRequest(new VirtueItemRequestEventArgs(state.Mobile, beheld, buttonID));
                    }
                }
            }
        }

        public static void SetWarMode(NetState state, PacketReader pvSrc)
        {
            state.Mobile.DelayChangeWarmode(pvSrc.ReadBoolean());
        }

        public static void Resynchronize(NetState state, PacketReader pvSrc)
        {
            Mobile m = state.Mobile;

            if (state.StygianAbyss)
            {
                state.Send(new MobileUpdate(m));
            }
            else
            {
                state.Send(new MobileUpdateOld(m));
            }

            state.Send(MobileIncoming.Create(state, m, m));

            m.SendEverything();

            state.Sequence = 0;

            m.ClearFastwalkStack();
        }

        private static int[] m_EmptyInts = new int[0];

        public static void AsciiSpeech(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            MessageType type = (MessageType)pvSrc.ReadByte();
            int hue = pvSrc.ReadInt16();
            pvSrc.ReadInt16(); // font
            string text = pvSrc.ReadStringSafe().Trim();

            if (text.Length <= 0 || text.Length > 128)
                return;

            if (!Enum.IsDefined(typeof(MessageType), type))
                type = MessageType.Regular;

            from.DoSpeech(text, m_EmptyInts, type, Utility.ClipDyedHue(hue));
        }

        private static KeywordList m_KeywordList = new KeywordList();

        public static void UnicodeSpeech(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            MessageType type = (MessageType)pvSrc.ReadByte();
            int hue = pvSrc.ReadInt16();
            pvSrc.ReadInt16(); // font
            string lang = pvSrc.ReadString(4);
            string text;

            bool isEncoded = (type & MessageType.Encoded) != 0;
            int[] keywords;

            if (isEncoded)
            {
                int value = pvSrc.ReadInt16();
                int count = (value & 0xFFF0) >> 4;
                int hold = value & 0xF;

                if (count < 0 || count > 50)
                    return;

                KeywordList keyList = m_KeywordList;

                for (int i = 0; i < count; ++i)
                {
                    int speechID;

                    if ((i & 1) == 0)
                    {
                        hold <<= 8;
                        hold |= pvSrc.ReadByte();
                        speechID = hold;
                        hold = 0;
                    }
                    else
                    {
                        value = pvSrc.ReadInt16();
                        speechID = (value & 0xFFF0) >> 4;
                        hold = value & 0xF;
                    }

                    if (!keyList.Contains(speechID))
                        keyList.Add(speechID);
                }

                text = pvSrc.ReadUTF8StringSafe();

                keywords = keyList.ToArray();
            }
            else
            {
                text = pvSrc.ReadUnicodeStringSafe();

                keywords = m_EmptyInts;
            }

            text = text.Trim();

            if (text.Length <= 0 || text.Length > 128)
                return;

            type &= ~MessageType.Encoded;

            if (!Enum.IsDefined(typeof(MessageType), type))
                type = MessageType.Regular;

            from.Language = lang;
            from.DoSpeech(text, keywords, type, Utility.ClipDyedHue(hue));
        }

        public static void UseReq(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            if (from.AccessLevel >= AccessLevel.Counselor || Core.TickCount - from.NextActionTime >= 0)
            {
                int value = pvSrc.ReadInt32();

                if ((value & ~0x7FFFFFFF) != 0)
                {
                    from.OnPaperdollRequest();
                }
                else
                {
                    Serial s = value;

                    if (s.IsMobile)
                    {
                        Mobile m = World.FindMobile(s);

                        if (m != null && !m.Deleted)
                            from.Use(m);
                    }
                    else if (s.IsItem)
                    {
                        Item item = World.FindItem(s);

                        if (item != null && !item.Deleted)
                            from.Use(item);
                    }
                }

                from.NextActionTime = Core.TickCount + Mobile.ActionDelay;
            }
            else
            {
                from.SendActionMessage();
            }
        }

        private static bool m_SingleClickProps;

        public static bool SingleClickProps
        {
            get { return m_SingleClickProps; }
            set { m_SingleClickProps = value; }
        }

        public static void LookReq(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            Serial s = pvSrc.ReadInt32();

            if (s.IsMobile)
            {
                Mobile m = World.FindMobile(s);

                if (m != null && from.CanSee(m) && Utility.InUpdateRange(from, m))
                {
                    if (m_SingleClickProps)
                    {
                        m.OnAosSingleClick(from);
                    }
                    else
                    {
                        if (from.Region.OnSingleClick(from, m))
                            m.OnSingleClick(from);
                    }
                }
            }
            else if (s.IsItem)
            {
                Item item = World.FindItem(s);

                if (item != null && !item.Deleted && from.CanSee(item) && Utility.InUpdateRange(from.Location, item.GetWorldLocation()))
                {
                    if (m_SingleClickProps)
                    {
                        item.OnAosSingleClick(from);
                    }
                    else if (from.Region.OnSingleClick(from, item))
                    {
                        if (item.Parent is Item)
                            ((Item)item.Parent).OnSingleClickContained(from, item);

                        item.OnSingleClick(from);
                    }
                }
            }
        }

        public static void PingReq(NetState state, PacketReader pvSrc)
        {
            state.Send(PingAck.Instantiate(pvSrc.ReadByte()));
        }

        public static void SetUpdateRange(NetState state, PacketReader pvSrc)
        {
            state.Send(ChangeUpdateRange.Instantiate(18));
        }

        private const int BadFood = unchecked((int)0xBAADF00D);
        private const int BadUOTD = unchecked((int)0xFFCEFFCE);

        public static void MovementReq(NetState state, PacketReader pvSrc)
        {
            Direction dir = (Direction)pvSrc.ReadByte();
            int seq = pvSrc.ReadByte();
            int key = pvSrc.ReadInt32();

            Mobile m = state.Mobile;

            if ((state.Sequence == 0 && seq != 0) || !m.Move(dir))
            {
                state.Send(new MovementRej(seq, m));
                state.Sequence = 0;

                m.ClearFastwalkStack();
            }
            else
            {
                ++seq;

                if (seq == 256)
                    seq = 1;

                state.Sequence = seq;
            }
        }

        public static int[] m_ValidAnimations = new int[]
            {
                6, 21, 32, 33,
                100, 101, 102,
                103, 104, 105,
                106, 107, 108,
                109, 110, 111,
                112, 113, 114,
                115, 116, 117,
                118, 119, 120,
                121, 123, 124,
                125, 126, 127,
                128
            };

        public static int[] ValidAnimations { get { return m_ValidAnimations; } set { m_ValidAnimations = value; } }

        public static void Animate(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;
            int action = pvSrc.ReadInt32();

            bool ok = false;

            for (int i = 0; !ok && i < m_ValidAnimations.Length; ++i)
                ok = (action == m_ValidAnimations[i]);

            if (from != null && ok && from.Alive && from.Body.IsHuman && !from.Mounted)
                from.Animate(action, 7, 1, true, false, 0);
        }

        public static void QuestArrow(NetState state, PacketReader pvSrc)
        {
            bool rightClick = pvSrc.ReadBoolean();
            Mobile from = state.Mobile;

            if (from != null && from.QuestArrow != null)
                from.QuestArrow.OnClick(rightClick);
        }

        public static void ExtendedCommand(NetState state, PacketReader pvSrc)
        {
            int packetID = pvSrc.ReadUInt16();

            PacketHandler ph = GetExtendedHandler(packetID);

            if (ph != null)
            {
                if (ph.Ingame && state.Mobile == null)
                {
                    Console.WriteLine("Client: {0}: Sent ingame packet (0xBFx{1:X2}) before having been attached to a mobile", state, packetID);
                    state.Dispose();
                }
                else if (ph.Ingame && state.Mobile.Deleted)
                {
                    state.Dispose();
                }
                else
                {
                    ph.OnReceive(state, pvSrc);
                }
            }
            else
            {
                pvSrc.Trace(state);
            }
        }

        public static void CastSpell(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            if (from == null)
                return;

            Item spellbook = null;

            if (pvSrc.ReadInt16() == 1)
                spellbook = World.FindItem(pvSrc.ReadInt32());

            int spellID = pvSrc.ReadInt16() - 1;

            EventSink.InvokeCastSpellRequest(new CastSpellRequestEventArgs(from, spellID, spellbook));
        }

        public static void BandageTarget(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            if (from == null)
                return;

            if (from.AccessLevel >= AccessLevel.Counselor || Core.TickCount - from.NextActionTime >= 0)
            {
                Item bandage = World.FindItem(pvSrc.ReadInt32());

                if (bandage == null)
                    return;

                Mobile target = World.FindMobile(pvSrc.ReadInt32());

                if (target == null)
                    return;

                EventSink.InvokeBandageTargetRequest(new BandageTargetRequestEventArgs(from, bandage, target));

                from.NextActionTime = Core.TickCount + Mobile.ActionDelay;
            }
            else
            {
                from.SendActionMessage();
            }
        }

        public static void ToggleFlying(NetState state, PacketReader pvSrc)
        {
            state.Mobile.ToggleFlying();
        }
        public static void BatchQueryProperties(NetState state, PacketReader pvSrc)
        {
            if (!ObjectPropertyList.Enabled)
                return;

            Mobile from = state.Mobile;

            int length = pvSrc.Size - 3;

            if (length < 0 || (length % 4) != 0)
                return;

            int count = length / 4;

            for (int i = 0; i < count; ++i)
            {
                Serial s = pvSrc.ReadInt32();

                if (s.IsMobile)
                {
                    Mobile m = World.FindMobile(s);

                    if (m != null && from.CanSee(m) && Utility.InUpdateRange(from, m))
                        m.SendPropertiesTo(from);
                }
                else if (s.IsItem)
                {
                    Item item = World.FindItem(s);

                    if (item != null && !item.Deleted && from.CanSee(item) && Utility.InUpdateRange(from.Location, item.GetWorldLocation()))
                        item.SendPropertiesTo(from);
                }
            }
        }

        public static void QueryProperties(NetState state, PacketReader pvSrc)
        {
            if (!ObjectPropertyList.Enabled)
                return;

            Mobile from = state.Mobile;

            Serial s = pvSrc.ReadInt32();

            if (s.IsMobile)
            {
                Mobile m = World.FindMobile(s);

                if (m != null && from.CanSee(m) && Utility.InUpdateRange(from, m))
                    m.SendPropertiesTo(from);
            }
            else if (s.IsItem)
            {
                Item item = World.FindItem(s);

                if (item != null && !item.Deleted && from.CanSee(item) && Utility.InUpdateRange(from.Location, item.GetWorldLocation()))
                    item.SendPropertiesTo(from);
            }
        }

        public static void PartyMessage(NetState state, PacketReader pvSrc)
        {
            if (state.Mobile == null)
                return;

            switch (pvSrc.ReadByte())
            {
                case 0x01: PartyMessage_AddMember(state, pvSrc); break;
                case 0x02: PartyMessage_RemoveMember(state, pvSrc); break;
                case 0x03: PartyMessage_PrivateMessage(state, pvSrc); break;
                case 0x04: PartyMessage_PublicMessage(state, pvSrc); break;
                case 0x06: PartyMessage_SetCanLoot(state, pvSrc); break;
                case 0x08: PartyMessage_Accept(state, pvSrc); break;
                case 0x09: PartyMessage_Decline(state, pvSrc); break;
                default: pvSrc.Trace(state); break;
            }
        }

        public static void PartyMessage_AddMember(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnAdd(state.Mobile);
        }

        public static void PartyMessage_RemoveMember(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnRemove(state.Mobile, World.FindMobile(pvSrc.ReadInt32()));
        }

        public static void PartyMessage_PrivateMessage(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnPrivateMessage(state.Mobile, World.FindMobile(pvSrc.ReadInt32()), pvSrc.ReadUnicodeStringSafe());
        }

        public static void PartyMessage_PublicMessage(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnPublicMessage(state.Mobile, pvSrc.ReadUnicodeStringSafe());
        }

        public static void PartyMessage_SetCanLoot(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnSetCanLoot(state.Mobile, pvSrc.ReadBoolean());
        }

        public static void PartyMessage_Accept(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnAccept(state.Mobile, World.FindMobile(pvSrc.ReadInt32()));
        }

        public static void PartyMessage_Decline(NetState state, PacketReader pvSrc)
        {
            if (PartyCommands.Handler != null)
                PartyCommands.Handler.OnDecline(state.Mobile, World.FindMobile(pvSrc.ReadInt32()));
        }

        public static void StunRequest(NetState state, PacketReader pvSrc)
        {
            EventSink.InvokeStunRequest(new StunRequestEventArgs(state.Mobile));
        }

        public static void DisarmRequest(NetState state, PacketReader pvSrc)
        {
            EventSink.InvokeDisarmRequest(new DisarmRequestEventArgs(state.Mobile));
        }

        public static void StatLockChange(NetState state, PacketReader pvSrc)
        {
            int stat = pvSrc.ReadByte();
            int lockValue = pvSrc.ReadByte();

            if (lockValue > 2) lockValue = 0;

            Mobile m = state.Mobile;

            if (m != null)
            {
                switch (stat)
                {
                    case 0: m.StrLock = (StatLockType)lockValue; break;
                    case 1: m.DexLock = (StatLockType)lockValue; break;
                    case 2: m.IntLock = (StatLockType)lockValue; break;
                }
            }
        }

        public static void ScreenSize(NetState state, PacketReader pvSrc)
        {
            int width = pvSrc.ReadInt32();
            int unk = pvSrc.ReadInt32();
        }

        public static void ContextMenuResponse(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            if (from != null)
            {
                ContextMenu menu = from.ContextMenu;

                from.ContextMenu = null;

                if (menu != null && from != null && from == menu.From)
                {
                    IEntity entity = World.FindEntity(pvSrc.ReadInt32());

                    if (entity != null && entity == menu.Target && from.CanSee(entity))
                    {
                        Point3D p;

                        if (entity is Mobile)
                            p = entity.Location;
                        else if (entity is Item)
                            p = ((Item)entity).GetWorldLocation();
                        else
                            return;

                        int index = pvSrc.ReadUInt16();

                        if (index >= 0 && index < menu.Entries.Length)
                        {
                            ContextMenuEntry e = menu.Entries[index];

                            int range = e.Range;

                            if (range == -1)
                                range = 18;

                            if (e.Enabled && from.InRange(p, range))
                                e.OnClick();
                        }
                    }
                }
            }
        }

        public static void ContextMenuRequest(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;
            IEntity target = World.FindEntity(pvSrc.ReadInt32());

            if (from != null && target != null && from.Map == target.Map && from.CanSee(target))
            {
                if (target is Mobile && !Utility.InUpdateRange(from.Location, target.Location))
                    return;
                else if (target is Item && !Utility.InUpdateRange(from.Location, ((Item)target).GetWorldLocation()))
                    return;

                if (!from.CheckContextMenuDisplay(target))
                    return;

                ContextMenu c = new ContextMenu(from, target);

                if (c.Entries.Length > 0)
                {
                    if (target is Item)
                    {
                        object root = ((Item)target).RootParent;

                        if (root is Mobile && root != from && ((Mobile)root).AccessLevel >= from.AccessLevel)
                        {
                            for (int i = 0; i < c.Entries.Length; ++i)
                            {
                                if (!c.Entries[i].NonLocalUse)
                                    c.Entries[i].Enabled = false;
                            }
                        }
                    }

                    from.ContextMenu = c;
                }
            }
        }

        public static void CloseStatus(NetState state, PacketReader pvSrc)
        {
            Serial serial = pvSrc.ReadInt32();
        }

        public static void Language(NetState state, PacketReader pvSrc)
        {
            string lang = pvSrc.ReadString(4);

            if (state.Mobile != null)
                state.Mobile.Language = lang;
        }

        public static void AssistVersion(NetState state, PacketReader pvSrc)
        {
            int unk = pvSrc.ReadInt32();
            string av = pvSrc.ReadString();
        }

        public static void ClientVersion(NetState state, PacketReader pvSrc)
        {
            CV version = state.Version = new CV(pvSrc.ReadString());

            EventSink.InvokeClientVersionReceived(new ClientVersionReceivedArgs(state, version));
        }

        public static void ClientType(NetState state, PacketReader pvSrc)
        {
            pvSrc.ReadUInt16();

            int type = pvSrc.ReadUInt16();
            CV version = state.Version = new CV(pvSrc.ReadString());

            //EventSink.InvokeClientVersionReceived( new ClientVersionReceivedArgs( state, version ) );//todo
        }

        public static void MobileQuery(NetState state, PacketReader pvSrc)
        {
            Mobile from = state.Mobile;

            pvSrc.ReadInt32(); // 0xEDEDEDED
            int type = pvSrc.ReadByte();
            Mobile m = World.FindMobile(pvSrc.ReadInt32());

            if (m != null)
            {
                switch (type)
                {
                    case 0x00: // Unknown, sent by godclient
                        {
                            if (VerifyGC(state))
                                Console.WriteLine("God Client: {0}: Query 0x{1:X2} on {2} '{3}'", state, type, m.Serial, m.Name);

                            break;
                        }
                    case 0x04: // Stats
                        {
                            m.OnStatsQuery(from);
                            break;
                        }
                    case 0x05:
                        {
                            m.OnSkillsQuery(from);
                            break;
                        }
                    default:
                        {
                            pvSrc.Trace(state);
                            break;
                        }
                }
            }
        }

        public delegate void PlayCharCallback(NetState state, bool val);

        public static PlayCharCallback ThirdPartyAuthCallback = null, ThirdPartyHackedCallback = null;

        private static byte[] m_ThirdPartyAuthKey = new byte[]
            {
                0x9, 0x11, 0x83, (byte)'+', 0x4, 0x17, 0x83,
                0x5, 0x24, 0x85,
                0x7, 0x17, 0x87,
                0x6, 0x19, 0x88,
            };

        private class LoginTimer : Timer
        {
            private NetState m_State;
            private Mobile m_Mobile;

            public LoginTimer(NetState state, Mobile m) : base(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.0))
            {
                m_State = state;
                m_Mobile = m;
            }

            protected override void OnTick()
            {
                if (m_State == null)
                    Stop();
                if (m_State.Version != null)
                {
                    m_State.BlockAllPackets = false;
                    DoLogin(m_State, m_Mobile);
                    Stop();
                }
            }
        }

        public static void PlayCharacter(NetState state, PacketReader pvSrc)
        {
            pvSrc.ReadInt32(); // 0xEDEDEDED

            string name = pvSrc.ReadString(30);

            pvSrc.Seek(2, SeekOrigin.Current);
            int flags = pvSrc.ReadInt32();

            if (FeatureProtection.DisabledFeatures != 0 && ThirdPartyAuthCallback != null)
            {
                bool authOK = false;

                ulong razorFeatures = (((ulong)pvSrc.ReadUInt32()) << 32) | ((ulong)pvSrc.ReadUInt32());

                if (razorFeatures == (ulong)FeatureProtection.DisabledFeatures)
                {
                    bool match = true;
                    for (int i = 0; match && i < m_ThirdPartyAuthKey.Length; i++)
                        match = match && pvSrc.ReadByte() == m_ThirdPartyAuthKey[i];

                    if (match)
                        authOK = true;
                }
                else
                {
                    pvSrc.Seek(16, SeekOrigin.Current);
                }

                ThirdPartyAuthCallback(state, authOK);
            }
            else
            {
                pvSrc.Seek(24, SeekOrigin.Current);
            }

            if (ThirdPartyHackedCallback != null)
            {
                pvSrc.Seek(-2, SeekOrigin.Current);
                if (pvSrc.ReadUInt16() == 0xDEAD)
                    ThirdPartyHackedCallback(state, true);
            }

            if (!state.Running)
                return;

            int charSlot = pvSrc.ReadInt32();
            int clientIP = pvSrc.ReadInt32();

            IAccount a = state.Account;

            if (a == null || charSlot < 0 || charSlot >= a.Length)
            {
                state.Dispose();
            }
            else
            {
                Mobile m = a[charSlot];

                // Check if anyone is using this account
                for (int i = 0; i < a.Length; ++i)
                {
                    Mobile check = a[i];

                    if (check != null && check.Map != Map.Internal && check != m)
                    {
                        Console.WriteLine("Login: {0}: Account in use", state);
                        state.Send(new PopupMessage(PMMessage.CharInWorld));
                        return;
                    }
                }

                if (m == null)
                {
                    state.Dispose();
                }
                else
                {
                    if (m.NetState != null)
                        m.NetState.Dispose();

                    NetState.ProcessDisposedQueue();

                    state.Send(new ClientVersionReq());

                    state.BlockAllPackets = true;

                    state.Flags = (ClientFlags)flags;

                    state.Mobile = m;
                    m.NetState = state;

                    new LoginTimer(state, m).Start();
                }
            }
        }

        public static void DoLogin(NetState state, Mobile m)
        {
            state.Send(new LoginConfirm(m));

            if (m.Map != null)
                state.Send(new MapChange(m));

            state.Send(new MapPatches());

            state.Send(SeasonChange.Instantiate(m.GetSeason(), true));

            state.Send(SupportedFeatures.Instantiate(state));

            state.Sequence = 0;

            if (state.NewMobileIncoming)
            {
                state.Send(new MobileUpdate(m));
                state.Send(new MobileUpdate(m));

                m.CheckLightLevels(true);

                state.Send(new MobileUpdate(m));

                state.Send(new MobileIncoming(m, m));
                //state.Send( new MobileAttributes( m ) );
                state.Send(new MobileStatus(m, m));
                state.Send(Server.Network.SetWarMode.Instantiate(m.Warmode));

                m.SendEverything();

                state.Send(SupportedFeatures.Instantiate(state));
                state.Send(new MobileUpdate(m));
                //state.Send( new MobileAttributes( m ) );
                state.Send(new MobileStatus(m, m));
                state.Send(Server.Network.SetWarMode.Instantiate(m.Warmode));
                state.Send(new MobileIncoming(m, m));
            }
            else if (state.StygianAbyss)
            {
                state.Send(new MobileUpdate(m));
                state.Send(new MobileUpdate(m));

                m.CheckLightLevels(true);

                state.Send(new MobileUpdate(m));

                state.Send(new MobileIncomingSA(m, m));
                //state.Send( new MobileAttributes( m ) );
                state.Send(new MobileStatus(m, m));
                state.Send(Server.Network.SetWarMode.Instantiate(m.Warmode));

                m.SendEverything();

                state.Send(SupportedFeatures.Instantiate(state));
                state.Send(new MobileUpdate(m));
                //state.Send( new MobileAttributes( m ) );
                state.Send(new MobileStatus(m, m));
                state.Send(Server.Network.SetWarMode.Instantiate(m.Warmode));
                state.Send(new MobileIncomingSA(m, m));
            }
            else
            {
                state.Send(new MobileUpdateOld(m));
                state.Send(new MobileUpdateOld(m));

                m.CheckLightLevels(true);

                state.Send(new MobileUpdateOld(m));

                state.Send(new MobileIncomingOld(m, m));
                //state.Send( new MobileAttributes( m ) );
                state.Send(new MobileStatus(m, m));
                state.Send(Server.Network.SetWarMode.Instantiate(m.Warmode));

                m.SendEverything();

                state.Send(SupportedFeatures.Instantiate(state));
                state.Send(new MobileUpdateOld(m));
                //state.Send( new MobileAttributes( m ) );
                state.Send(new MobileStatus(m, m));
                state.Send(Server.Network.SetWarMode.Instantiate(m.Warmode));
                state.Send(new MobileIncomingOld(m, m));
            }

            state.Send(LoginComplete.Instance);
            state.Send(new CurrentTime());
            state.Send(SeasonChange.Instantiate(m.GetSeason(), true));
            state.Send(new MapChange(m));

            EventSink.InvokeLogin(new LoginEventArgs(m));

            m.ClearFastwalkStack();
        }

        public static void CreateCharacter(NetState state, PacketReader pvSrc)
        {
            int unk1 = pvSrc.ReadInt32();
            int unk2 = pvSrc.ReadInt32();
            int unk3 = pvSrc.ReadByte();
            string name = pvSrc.ReadString(30);

            pvSrc.Seek(2, SeekOrigin.Current);
            int flags = pvSrc.ReadInt32();
            pvSrc.Seek(8, SeekOrigin.Current);
            int prof = pvSrc.ReadByte();
            pvSrc.Seek(15, SeekOrigin.Current);

            //bool female = pvSrc.ReadBoolean();

            int genderRace = pvSrc.ReadByte();

            int str = pvSrc.ReadByte();
            int dex = pvSrc.ReadByte();
            int intl = pvSrc.ReadByte();
            int is1 = pvSrc.ReadByte();
            int vs1 = pvSrc.ReadByte();
            int is2 = pvSrc.ReadByte();
            int vs2 = pvSrc.ReadByte();
            int is3 = pvSrc.ReadByte();
            int vs3 = pvSrc.ReadByte();
            int hue = pvSrc.ReadUInt16();
            int hairVal = pvSrc.ReadInt16();
            int hairHue = pvSrc.ReadInt16();
            int hairValf = pvSrc.ReadInt16();
            int hairHuef = pvSrc.ReadInt16();
            pvSrc.ReadByte();
            int cityIndex = pvSrc.ReadByte();
            int charSlot = pvSrc.ReadInt32();
            int clientIP = pvSrc.ReadInt32();
            int shirtHue = pvSrc.ReadInt16();
            int pantsHue = pvSrc.ReadInt16();

            /*
			Pre-7.0.0.0:
			0x00, 0x01 -> Human Male, Human Female
			0x02, 0x03 -> Elf Male, Elf Female

			Post-7.0.0.0:
			0x00, 0x01
			0x02, 0x03 -> Human Male, Human Female
			0x04, 0x05 -> Elf Male, Elf Female
			0x05, 0x06 -> Gargoyle Male, Gargoyle Female
			*/

            bool female = ((genderRace % 2) != 0);

            Race race = null;

            if (state.StygianAbyss)
            {
                byte raceID = (byte)(genderRace < 4 ? 0 : ((genderRace / 2) - 1));
                race = Race.Races[raceID];
            }
            else
            {
                race = Race.Races[(byte)(genderRace / 2)];
            }

            if (race == null)
                race = Race.DefaultRace;

            CityInfo[] info = state.CityInfo;
            IAccount a = state.Account;

            if (info == null || a == null || cityIndex < 0 || cityIndex >= info.Length)
            {
                state.Dispose();
            }
            else
            {
                // Check if anyone is using this account
                for (int i = 0; i < a.Length; ++i)
                {
                    Mobile check = a[i];

                    if (check != null && check.Map != Map.Internal)
                    {
                        Console.WriteLine("Login: {0}: Account in use", state);
                        state.Send(new PopupMessage(PMMessage.CharInWorld));
                        return;
                    }
                }

                state.Flags = (ClientFlags)flags;

                CharacterCreatedEventArgs args = new CharacterCreatedEventArgs(
                    state, a,
                    name, female, hue,
                    str, dex, intl,
                    info[cityIndex],
                    new SkillNameValue[3]
                    {
                        new SkillNameValue( (SkillName)is1, vs1 ),
                        new SkillNameValue( (SkillName)is2, vs2 ),
                        new SkillNameValue( (SkillName)is3, vs3 ),
                    },
                    shirtHue, pantsHue,
                    hairVal, hairHue,
                    hairValf, hairHuef,
                    prof,
                    race
                    );

                state.Send(new ClientVersionReq());

                state.BlockAllPackets = true;

                EventSink.InvokeCharacterCreated(args);

                Mobile m = args.Mobile;

                if (m != null)
                {
                    state.Mobile = m;
                    m.NetState = state;
                    new LoginTimer(state, m).Start();
                }
                else
                {
                    state.BlockAllPackets = false;
                    state.Dispose();
                }
            }
        }

        public static void CreateCharacter70160(NetState state, PacketReader pvSrc)
        {
            int unk1 = pvSrc.ReadInt32();
            int unk2 = pvSrc.ReadInt32();
            int unk3 = pvSrc.ReadByte();
            string name = pvSrc.ReadString(30);

            pvSrc.Seek(2, SeekOrigin.Current);
            int flags = pvSrc.ReadInt32();
            pvSrc.Seek(8, SeekOrigin.Current);
            int prof = pvSrc.ReadByte();
            pvSrc.Seek(15, SeekOrigin.Current);

            int genderRace = pvSrc.ReadByte();

            int str = pvSrc.ReadByte();
            int dex = pvSrc.ReadByte();
            int intl = pvSrc.ReadByte();
            int is1 = pvSrc.ReadByte();
            int vs1 = pvSrc.ReadByte();
            int is2 = pvSrc.ReadByte();
            int vs2 = pvSrc.ReadByte();
            int is3 = pvSrc.ReadByte();
            int vs3 = pvSrc.ReadByte();
            int is4 = pvSrc.ReadByte();
            int vs4 = pvSrc.ReadByte();

            int hue = pvSrc.ReadUInt16();
            int hairVal = pvSrc.ReadInt16();
            int hairHue = pvSrc.ReadInt16();
            int hairValf = pvSrc.ReadInt16();
            int hairHuef = pvSrc.ReadInt16();
            pvSrc.ReadByte();
            int cityIndex = pvSrc.ReadByte();
            int charSlot = pvSrc.ReadInt32();
            int clientIP = pvSrc.ReadInt32();
            int shirtHue = pvSrc.ReadInt16();
            int pantsHue = pvSrc.ReadInt16();

            /*
			0x00, 0x01
			0x02, 0x03 -> Human Male, Human Female
			0x04, 0x05 -> Elf Male, Elf Female
			0x05, 0x06 -> Gargoyle Male, Gargoyle Female
			*/

            bool female = ((genderRace % 2) != 0);

            Race race = null;

            byte raceID = (byte)(genderRace < 4 ? 0 : ((genderRace / 2) - 1));
            race = Race.Races[raceID];

            if (race == null)
                race = Race.DefaultRace;

            CityInfo[] info = state.CityInfo;
            IAccount a = state.Account;

            if (info == null || a == null || cityIndex < 0 || cityIndex >= info.Length)
            {
                state.Dispose();
            }
            else
            {
                // Check if anyone is using this account
                for (int i = 0; i < a.Length; ++i)
                {
                    Mobile check = a[i];

                    if (check != null && check.Map != Map.Internal)
                    {
                        Console.WriteLine("Login: {0}: Account in use", state);
                        state.Send(new PopupMessage(PMMessage.CharInWorld));
                        return;
                    }
                }

                state.Flags = (ClientFlags)flags;

                CharacterCreatedEventArgs args = new CharacterCreatedEventArgs(
                    state, a,
                    name, female, hue,
                    str, dex, intl,
                    info[cityIndex],
                    new SkillNameValue[4]
                    {
                        new SkillNameValue( (SkillName)is1, vs1 ),
                        new SkillNameValue( (SkillName)is2, vs2 ),
                        new SkillNameValue( (SkillName)is3, vs3 ),
                        new SkillNameValue( (SkillName)is4, vs4 ),
                    },
                    shirtHue, pantsHue,
                    hairVal, hairHue,
                    hairValf, hairHuef,
                    prof,
                    race
                    );

                state.Send(new ClientVersionReq());

                state.BlockAllPackets = true;

                EventSink.InvokeCharacterCreated(args);

                Mobile m = args.Mobile;

                if (m != null)
                {
                    state.Mobile = m;
                    m.NetState = state;
                    new LoginTimer(state, m).Start();
                }
                else
                {
                    state.BlockAllPackets = false;
                    state.Dispose();
                }
            }
        }

        private static bool m_ClientVerification = true;

        public static bool ClientVerification
        {
            get { return m_ClientVerification; }
            set { m_ClientVerification = value; }
        }

        internal struct AuthIDPersistence
        {
            public DateTime Age;
            public ClientVersion Version;

            public AuthIDPersistence(ClientVersion v)
            {
                Age = DateTime.UtcNow;
                Version = v;
            }
        }

        private const int m_AuthIDWindowSize = 128;
        private static Dictionary<int, AuthIDPersistence> m_AuthIDWindow = new Dictionary<int, AuthIDPersistence>(m_AuthIDWindowSize);

        private static int GenerateAuthID(NetState state)
        {
            if (m_AuthIDWindow.Count == m_AuthIDWindowSize)
            {
                int oldestID = 0;
                DateTime oldest = DateTime.MaxValue;

                foreach (KeyValuePair<int, AuthIDPersistence> kvp in m_AuthIDWindow)
                {
                    if (kvp.Value.Age < oldest)
                    {
                        oldestID = kvp.Key;
                        oldest = kvp.Value.Age;
                    }
                }

                m_AuthIDWindow.Remove(oldestID);
            }

            int authID;

            do
            {
                authID = Utility.Random(1, int.MaxValue - 1);

                if (Utility.RandomBool())
                    authID |= 1 << 31;
            } while (m_AuthIDWindow.ContainsKey(authID));

            m_AuthIDWindow[authID] = new AuthIDPersistence(state.Version);

            return authID;
        }

        public static void GameLogin(NetState state, PacketReader pvSrc)
        {
            if (state.SentFirstPacket)
            {
                state.Dispose();
                return;
            }

            state.SentFirstPacket = true;

            int authID = pvSrc.ReadInt32();

            if (m_AuthIDWindow.ContainsKey(authID))
            {
                AuthIDPersistence ap = m_AuthIDWindow[authID];
                m_AuthIDWindow.Remove(authID);

                state.Version = ap.Version;
            }
            else if (m_ClientVerification)
            {
                Console.WriteLine("Login: {0}: Invalid client detected, disconnecting", state);
                state.Dispose();
                return;
            }

            if (state.m_AuthID != 0 && authID != state.m_AuthID)
            {
                Console.WriteLine("Login: {0}: Invalid client detected, disconnecting", state);
                state.Dispose();
                return;
            }
            else if (state.m_AuthID == 0 && authID != state.m_Seed)
            {
                Console.WriteLine("Login: {0}: Invalid client detected, disconnecting", state);
                state.Dispose();
                return;
            }

            string username = pvSrc.ReadString(30);
            string password = pvSrc.ReadString(30);

            GameLoginEventArgs e = new GameLoginEventArgs(state, username, password);

            EventSink.InvokeGameLogin(e);

            if (e.Accepted)
            {
                state.CityInfo = e.CityInfo;
                state.CompressionEnabled = true;

                state.Send(SupportedFeatures.Instantiate(state));

                if (state.NewCharacterList)
                {
                    state.Send(new CharacterList(state.Account, state.CityInfo));
                }
                else
                {
                    state.Send(new CharacterListOld(state.Account, state.CityInfo));
                }
            }
            else
            {
                state.Dispose();
            }
        }

        public static void PlayServer(NetState state, PacketReader pvSrc)
        {
            int index = pvSrc.ReadInt16();
            ServerInfo[] info = state.ServerInfo;
            IAccount a = state.Account;

            if (info == null || a == null || index < 0 || index >= info.Length)
            {
                state.Dispose();
            }
            else
            {
                ServerInfo si = info[index];

                state.m_AuthID = PlayServerAck.m_AuthID = GenerateAuthID(state);

                state.SentFirstPacket = false;
                state.Send(new PlayServerAck(si));
            }
        }

        public static void LoginServerSeed(NetState state, PacketReader pvSrc)
        {
            state.m_Seed = pvSrc.ReadInt32();
            state.Seeded = true;

            if (state.m_Seed == 0)
            {
                Console.WriteLine("Login: {0}: Invalid client detected, disconnecting", state);
                state.Dispose();
                return;
            }

            int clientMaj = pvSrc.ReadInt32();
            int clientMin = pvSrc.ReadInt32();
            int clientRev = pvSrc.ReadInt32();
            int clientPat = pvSrc.ReadInt32();

            state.Version = new ClientVersion(clientMaj, clientMin, clientRev, clientPat);
        }

        public static void CrashReport(NetState state, PacketReader pvSrc)
        {
            byte clientMaj = pvSrc.ReadByte();
            byte clientMin = pvSrc.ReadByte();
            byte clientRev = pvSrc.ReadByte();
            byte clientPat = pvSrc.ReadByte();

            ushort x = pvSrc.ReadUInt16();
            ushort y = pvSrc.ReadUInt16();
            sbyte z = pvSrc.ReadSByte();
            byte map = pvSrc.ReadByte();

            string account = pvSrc.ReadString(32);
            string character = pvSrc.ReadString(32);
            string ip = pvSrc.ReadString(15);

            int unk1 = pvSrc.ReadInt32();
            int exception = pvSrc.ReadInt32();

            string process = pvSrc.ReadString(100);
            string report = pvSrc.ReadString(100);

            pvSrc.ReadByte(); // 0x00

            int offset = pvSrc.ReadInt32();

            int count = (int)pvSrc.ReadByte();

            for (int i = 0; i < count; i++)
            {
                int address = pvSrc.ReadInt32();
            }
        }

        public static void AccountLogin(NetState state, PacketReader pvSrc)
        {
            if (state.SentFirstPacket)
            {
                state.Dispose();
                return;
            }

            state.SentFirstPacket = true;

            string username = pvSrc.ReadString(30);
            string password = pvSrc.ReadString(30);

            AccountLoginEventArgs e = new AccountLoginEventArgs(state, username, password);

            EventSink.InvokeAccountLogin(e);

            if (e.Accepted)
                AccountLogin_ReplyAck(state);
            else
                AccountLogin_ReplyRej(state, e.RejectReason);
        }

        public static void AccountLogin_ReplyAck(NetState state)
        {
            ServerListEventArgs e = new ServerListEventArgs(state, state.Account);

            EventSink.InvokeServerList(e);

            if (e.Rejected)
            {
                state.Account = null;
                state.Send(new AccountLoginRej(ALRReason.BadComm));
                state.Dispose();
            }
            else
            {
                ServerInfo[] info = e.Servers.ToArray();

                state.ServerInfo = info;

                state.Send(new AccountLoginAck(info));
            }
        }

        public static void AccountLogin_ReplyRej(NetState state, ALRReason reason)
        {
            state.Send(new AccountLoginRej(reason));
            state.Dispose();
        }
    }

    public class PacketReader
    {
        private byte[] m_Data;
        private int m_Size;
        private int m_Index;

        public PacketReader(byte[] data, int size, bool fixedSize)
        {
            m_Data = data;
            m_Size = size;
            m_Index = fixedSize ? 1 : 3;
        }

        public byte[] Buffer
        {
            get
            {
                return m_Data;
            }
        }

        public int Size
        {
            get
            {
                return m_Size;
            }
        }

        public void Trace(NetState state)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter("Packets.log", true))
                {
                    byte[] buffer = m_Data;

                    if (buffer.Length > 0)
                        sw.WriteLine("Client: {0}: Unhandled packet 0x{1:X2}", state, buffer[0]);

                    using (MemoryStream ms = new MemoryStream(buffer))
                        Utility.FormatBuffer(sw, ms, buffer.Length);

                    sw.WriteLine();
                    sw.WriteLine();
                }
            }
            catch
            {
            }
        }

        public int Seek(int offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: m_Index = offset; break;
                case SeekOrigin.Current: m_Index += offset; break;
                case SeekOrigin.End: m_Index = m_Size - offset; break;
            }

            return m_Index;
        }

        public int ReadInt32()
        {
            if ((m_Index + 4) > m_Size)
                return 0;

            return (m_Data[m_Index++] << 24)
                 | (m_Data[m_Index++] << 16)
                 | (m_Data[m_Index++] << 8)
                 | m_Data[m_Index++];
        }

        public short ReadInt16()
        {
            if ((m_Index + 2) > m_Size)
                return 0;

            return (short)((m_Data[m_Index++] << 8) | m_Data[m_Index++]);
        }

        public byte ReadByte()
        {
            if ((m_Index + 1) > m_Size)
                return 0;

            return m_Data[m_Index++];
        }

        public uint ReadUInt32()
        {
            if ((m_Index + 4) > m_Size)
                return 0;

            return (uint)((m_Data[m_Index++] << 24) | (m_Data[m_Index++] << 16) | (m_Data[m_Index++] << 8) | m_Data[m_Index++]);
        }

        public ushort ReadUInt16()
        {
            if ((m_Index + 2) > m_Size)
                return 0;

            return (ushort)((m_Data[m_Index++] << 8) | m_Data[m_Index++]);
        }

        public sbyte ReadSByte()
        {
            if ((m_Index + 1) > m_Size)
                return 0;

            return (sbyte)m_Data[m_Index++];
        }

        public bool ReadBoolean()
        {
            if ((m_Index + 1) > m_Size)
                return false;

            return (m_Data[m_Index++] != 0);
        }

        public string ReadUnicodeStringLE()
        {
            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < m_Size && (c = (m_Data[m_Index++] | (m_Data[m_Index++] << 8))) != 0)
                sb.Append((char)c);

            return sb.ToString();
        }

        public string ReadUnicodeStringLESafe(int fixedLength)
        {
            int bound = m_Index + (fixedLength << 1);
            int end = bound;

            if (bound > m_Size)
                bound = m_Size;

            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < bound && (c = (m_Data[m_Index++] | (m_Data[m_Index++] << 8))) != 0)
            {
                if (IsSafeChar(c))
                    sb.Append((char)c);
            }

            m_Index = end;

            return sb.ToString();
        }

        public string ReadUnicodeStringLESafe()
        {
            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < m_Size && (c = (m_Data[m_Index++] | (m_Data[m_Index++] << 8))) != 0)
            {
                if (IsSafeChar(c))
                    sb.Append((char)c);
            }

            return sb.ToString();
        }

        public string ReadUnicodeStringSafe()
        {
            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < m_Size && (c = ((m_Data[m_Index++] << 8) | m_Data[m_Index++])) != 0)
            {
                if (IsSafeChar(c))
                    sb.Append((char)c);
            }

            return sb.ToString();
        }

        public string ReadUnicodeString()
        {
            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < m_Size && (c = ((m_Data[m_Index++] << 8) | m_Data[m_Index++])) != 0)
                sb.Append((char)c);

            return sb.ToString();
        }

        public bool IsSafeChar(int c)
        {
            return (c >= 0x20 && c < 0xFFFE);
        }

        public string ReadUTF8StringSafe(int fixedLength)
        {
            if (m_Index >= m_Size)
            {
                m_Index += fixedLength;
                return String.Empty;
            }

            int bound = m_Index + fixedLength;
            //int end   = bound;

            if (bound > m_Size)
                bound = m_Size;

            int count = 0;
            int index = m_Index;
            int start = m_Index;

            while (index < bound && m_Data[index++] != 0)
                ++count;

            index = 0;

            byte[] buffer = new byte[count];
            int value = 0;

            while (m_Index < bound && (value = m_Data[m_Index++]) != 0)
                buffer[index++] = (byte)value;

            string s = Utility.UTF8.GetString(buffer);

            bool isSafe = true;

            for (int i = 0; isSafe && i < s.Length; ++i)
                isSafe = IsSafeChar((int)s[i]);

            m_Index = start + fixedLength;

            if (isSafe)
                return s;

            StringBuilder sb = new StringBuilder(s.Length);

            for (int i = 0; i < s.Length; ++i)
                if (IsSafeChar((int)s[i]))
                    sb.Append(s[i]);

            return sb.ToString();
        }

        public string ReadUTF8StringSafe()
        {
            if (m_Index >= m_Size)
                return String.Empty;

            int count = 0;
            int index = m_Index;

            while (index < m_Size && m_Data[index++] != 0)
                ++count;

            index = 0;

            byte[] buffer = new byte[count];
            int value = 0;

            while (m_Index < m_Size && (value = m_Data[m_Index++]) != 0)
                buffer[index++] = (byte)value;

            string s = Utility.UTF8.GetString(buffer);

            bool isSafe = true;

            for (int i = 0; isSafe && i < s.Length; ++i)
                isSafe = IsSafeChar((int)s[i]);

            if (isSafe)
                return s;

            StringBuilder sb = new StringBuilder(s.Length);

            for (int i = 0; i < s.Length; ++i)
            {
                if (IsSafeChar((int)s[i]))
                    sb.Append(s[i]);
            }

            return sb.ToString();
        }

        public string ReadUTF8String()
        {
            if (m_Index >= m_Size)
                return String.Empty;

            int count = 0;
            int index = m_Index;

            while (index < m_Size && m_Data[index++] != 0)
                ++count;

            index = 0;

            byte[] buffer = new byte[count];
            int value = 0;

            while (m_Index < m_Size && (value = m_Data[m_Index++]) != 0)
                buffer[index++] = (byte)value;

            return Utility.UTF8.GetString(buffer);
        }

        public string ReadString()
        {
            StringBuilder sb = new StringBuilder();

            int c;

            while (m_Index < m_Size && (c = m_Data[m_Index++]) != 0)
                sb.Append((char)c);

            return sb.ToString();
        }

        public string ReadStringSafe()
        {
            StringBuilder sb = new StringBuilder();

            int c;

            while (m_Index < m_Size && (c = m_Data[m_Index++]) != 0)
            {
                if (IsSafeChar(c))
                    sb.Append((char)c);
            }

            return sb.ToString();
        }

        public string ReadUnicodeStringSafe(int fixedLength)
        {
            int bound = m_Index + (fixedLength << 1);
            int end = bound;

            if (bound > m_Size)
                bound = m_Size;

            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < bound && (c = ((m_Data[m_Index++] << 8) | m_Data[m_Index++])) != 0)
            {
                if (IsSafeChar(c))
                    sb.Append((char)c);
            }

            m_Index = end;

            return sb.ToString();
        }

        public string ReadUnicodeString(int fixedLength)
        {
            int bound = m_Index + (fixedLength << 1);
            int end = bound;

            if (bound > m_Size)
                bound = m_Size;

            StringBuilder sb = new StringBuilder();

            int c;

            while ((m_Index + 1) < bound && (c = ((m_Data[m_Index++] << 8) | m_Data[m_Index++])) != 0)
                sb.Append((char)c);

            m_Index = end;

            return sb.ToString();
        }

        public string ReadStringSafe(int fixedLength)
        {
            int bound = m_Index + fixedLength;
            int end = bound;

            if (bound > m_Size)
                bound = m_Size;

            StringBuilder sb = new StringBuilder();

            int c;

            while (m_Index < bound && (c = m_Data[m_Index++]) != 0)
            {
                if (IsSafeChar(c))
                    sb.Append((char)c);
            }

            m_Index = end;

            return sb.ToString();
        }

        public string ReadString(int fixedLength)
        {
            int bound = m_Index + fixedLength;
            int end = bound;

            if (bound > m_Size)
                bound = m_Size;

            StringBuilder sb = new StringBuilder();

            int c;

            while (m_Index < bound && (c = m_Data[m_Index++]) != 0)
                sb.Append((char)c);

            m_Index = end;

            return sb.ToString();
        }
    }

    public enum PMMessage : byte
    {
        CharNoExist = 1,
        CharExists = 2,
        CharInWorld = 5,
        LoginSyncError = 6,
        IdleWarning = 7
    }

    public enum LRReason : byte
    {
        CannotLift = 0,
        OutOfRange = 1,
        OutOfSight = 2,
        TryToSteal = 3,
        AreHolding = 4,
        Inspecific = 5
    }

    /*public enum CMEFlags
	{
		None = 0x00,
		Locked = 0x01,
		Arrow = 0x02,
		x0004 = 0x04,
		Color = 0x20,
		x0040 = 0x40,
		x0080 = 0x80
	}*/

    public sealed class DamagePacketOld : Packet
    {
        public DamagePacketOld(Mobile m, int amount) : base(0xBF)
        {
            EnsureCapacity(11);

            m_Stream.Write((short)0x22);
            m_Stream.Write((byte)1);
            m_Stream.Write((int)m.Serial);

            if (amount > 255)
                amount = 255;
            else if (amount < 0)
                amount = 0;

            m_Stream.Write((byte)amount);
        }
    }

    public sealed class DamagePacket : Packet
    {
        public DamagePacket(Mobile m, int amount) : base(0x0B, 7)
        {
            m_Stream.Write((int)m.Serial);

            if (amount > 0xFFFF)
                amount = 0xFFFF;
            else if (amount < 0)
                amount = 0;

            m_Stream.Write((ushort)amount);
        }

        /*public DamagePacket( Mobile m, int amount ) : base( 0xBF )
		{
			EnsureCapacity( 11 );

			m_Stream.Write( (short) 0x22 );
			m_Stream.Write( (byte) 1 );
			m_Stream.Write( (int) m.Serial );

			if ( amount > 255 )
				amount = 255;
			else if ( amount < 0 )
				amount = 0;

			m_Stream.Write( (byte)amount );
		}*/
    }

    public sealed class CancelArrow : Packet
    {
        public CancelArrow() : base(0xBA, 6)
        {
            m_Stream.Write((byte)0);
            m_Stream.Write((short)-1);
            m_Stream.Write((short)-1);
        }
    }

    public sealed class SetArrow : Packet
    {
        public SetArrow(int x, int y) : base(0xBA, 6)
        {
            m_Stream.Write((byte)1);
            m_Stream.Write((short)x);
            m_Stream.Write((short)y);
        }
    }

    public sealed class CancelArrowHS : Packet
    {
        public CancelArrowHS(int x, int y, Serial s) : base(0xBA, 10)
        {
            m_Stream.Write((byte)0);
            m_Stream.Write((short)x);
            m_Stream.Write((short)y);
            m_Stream.Write((int)s);
        }
    }

    public sealed class SetArrowHS : Packet
    {
        public SetArrowHS(int x, int y, Serial s) : base(0xBA, 10)
        {
            m_Stream.Write((byte)1);
            m_Stream.Write((short)x);
            m_Stream.Write((short)y);
            m_Stream.Write((int)s);
        }
    }

    public sealed class DisplaySecureTrade : Packet
    {
        public DisplaySecureTrade(Mobile them, Container first, Container second, string name)
            : base(0x6F)
        {
            if (name == null)
            {
                name = "";
            }

            EnsureCapacity(18 + name.Length);

            m_Stream.Write((byte)0); // Display
            m_Stream.Write(them.Serial);
            m_Stream.Write(first.Serial);
            m_Stream.Write(second.Serial);
            m_Stream.Write(true);

            m_Stream.WriteAsciiFixed(name, 30);
        }
    }

    public sealed class CloseSecureTrade : Packet
    {
        public CloseSecureTrade(Container cont)
            : base(0x6F)
        {
            EnsureCapacity(8);

            m_Stream.Write((byte)1); // Close
            m_Stream.Write(cont.Serial);
        }
    }

    public enum TradeFlag : byte
    {
        Display = 0x0,
        Close = 0x1,
        Update = 0x2,
        UpdateGold = 0x3,
        UpdateLedger = 0x4
    }

    public sealed class UpdateSecureTrade : Packet
    {
        public UpdateSecureTrade(Container cont, bool first, bool second)
            : this(cont, TradeFlag.Update, first ? 1 : 0, second ? 1 : 0)
        { }

        public UpdateSecureTrade(Container cont, TradeFlag flag, int first, int second)
            : base(0x6F)
        {
            EnsureCapacity(17);

            m_Stream.Write((byte)flag);
            m_Stream.Write(cont.Serial);
            m_Stream.Write(first);
            m_Stream.Write(second);
        }
    }

    public sealed class SecureTradeEquip : Packet
    {
        public SecureTradeEquip(Item item, Mobile m) : base(0x25, 20)
        {
            m_Stream.Write((int)item.Serial);
            m_Stream.Write((short)item.ItemID);
            m_Stream.Write((byte)0);
            m_Stream.Write((short)item.Amount);
            m_Stream.Write((short)item.X);
            m_Stream.Write((short)item.Y);
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)item.Hue);
        }
    }

    public sealed class SecureTradeEquip6017 : Packet
    {
        public SecureTradeEquip6017(Item item, Mobile m) : base(0x25, 21)
        {
            m_Stream.Write((int)item.Serial);
            m_Stream.Write((short)item.ItemID);
            m_Stream.Write((byte)0);
            m_Stream.Write((short)item.Amount);
            m_Stream.Write((short)item.X);
            m_Stream.Write((short)item.Y);
            m_Stream.Write((byte)0); // Grid Location?
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)item.Hue);
        }
    }

    public sealed class MapPatches : Packet
    {
        public MapPatches() : base(0xBF)
        {
            EnsureCapacity(9 + (3 * 8));

            m_Stream.Write((short)0x0018);

            m_Stream.Write((int)4);

            m_Stream.Write((int)Map.Felucca.Tiles.Patch.StaticBlocks);
            m_Stream.Write((int)Map.Felucca.Tiles.Patch.LandBlocks);

            m_Stream.Write((int)Map.Trammel.Tiles.Patch.StaticBlocks);
            m_Stream.Write((int)Map.Trammel.Tiles.Patch.LandBlocks);

            m_Stream.Write((int)Map.Ilshenar.Tiles.Patch.StaticBlocks);
            m_Stream.Write((int)Map.Ilshenar.Tiles.Patch.LandBlocks);

            m_Stream.Write((int)Map.Malas.Tiles.Patch.StaticBlocks);
            m_Stream.Write((int)Map.Malas.Tiles.Patch.LandBlocks);
        }
    }

    public sealed class ObjectHelpResponse : Packet
    {
        public ObjectHelpResponse(IEntity e, string text) : base(0xB7)
        {
            this.EnsureCapacity(9 + (text.Length * 2));

            m_Stream.Write((int)e.Serial);
            m_Stream.WriteBigUniNull(text);
        }
    }

    public sealed class VendorBuyContent : Packet
    {
        public VendorBuyContent(List<BuyItemState> list)
            : base(0x3c)
        {
            this.EnsureCapacity(list.Count * 19 + 5);

            m_Stream.Write((short)list.Count);

            //The client sorts these by their X/Y value.
            //OSI sends these in wierd order.  X/Y highest to lowest and serial loest to highest
            //These are already sorted by serial (done by the vendor class) but we have to send them by x/y
            //(the x74 packet is sent in 'correct' order.)
            for (int i = list.Count - 1; i >= 0; --i)
            {
                BuyItemState bis = (BuyItemState)list[i];

                m_Stream.Write((int)bis.MySerial);
                m_Stream.Write((ushort)bis.ItemID);
                m_Stream.Write((byte)0);//itemid offset
                m_Stream.Write((ushort)bis.Amount);
                m_Stream.Write((short)(i + 1));//x
                m_Stream.Write((short)1);//y
                m_Stream.Write((int)bis.ContainerSerial);
                m_Stream.Write((ushort)bis.Hue);
            }
        }
    }

    public sealed class VendorBuyContent6017 : Packet
    {
        public VendorBuyContent6017(List<BuyItemState> list) : base(0x3c)
        {
            this.EnsureCapacity(list.Count * 20 + 5);

            m_Stream.Write((short)list.Count);

            //The client sorts these by their X/Y value.
            //OSI sends these in wierd order.  X/Y highest to lowest and serial loest to highest
            //These are already sorted by serial (done by the vendor class) but we have to send them by x/y
            //(the x74 packet is sent in 'correct' order.)
            for (int i = list.Count - 1; i >= 0; --i)
            {
                BuyItemState bis = (BuyItemState)list[i];

                m_Stream.Write((int)bis.MySerial);
                m_Stream.Write((ushort)bis.ItemID);
                m_Stream.Write((byte)0);//itemid offset
                m_Stream.Write((ushort)bis.Amount);
                m_Stream.Write((short)(i + 1));//x
                m_Stream.Write((short)1);//y
                m_Stream.Write((byte)0); // Grid Location?
                m_Stream.Write((int)bis.ContainerSerial);
                m_Stream.Write((ushort)bis.Hue);
            }
        }
    }

    public sealed class DisplayBuyList : Packet
    {
        public DisplayBuyList(Mobile vendor) : base(0x24, 7)
        {
            m_Stream.Write((int)vendor.Serial);
            m_Stream.Write((short)0x30); // buy window id?
        }
    }

    public sealed class DisplayBuyListHS : Packet
    {
        public DisplayBuyListHS(Mobile vendor) : base(0x24, 9)
        {
            m_Stream.Write((int)vendor.Serial);
            m_Stream.Write((short)0x30); // buy window id?
            m_Stream.Write((short)0x00);
        }
    }

    public sealed class VendorBuyList : Packet
    {
        public VendorBuyList(Mobile vendor, List<BuyItemState> list)
            : base(0x74)
        {
            this.EnsureCapacity(256);

            Container BuyPack = vendor.FindItemOnLayer(Layer.ShopBuy) as Container;
            m_Stream.Write((int)(BuyPack == null ? Serial.MinusOne : BuyPack.Serial));

            m_Stream.Write((byte)list.Count);

            for (int i = 0; i < list.Count; ++i)
            {
                BuyItemState bis = list[i];

                m_Stream.Write((int)bis.Price);

                string desc = bis.Description;

                if (desc == null)
                    desc = "";

                m_Stream.Write((byte)(desc.Length + 1));
                m_Stream.WriteAsciiNull(desc);
            }
        }
    }

    public sealed class VendorSellList : Packet
    {
        public VendorSellList(Mobile shopkeeper, ICollection<SellItemState> sis) : base(0x9E)
        {
            this.EnsureCapacity(256);

            m_Stream.Write((int)shopkeeper.Serial);

            m_Stream.Write((ushort)sis.Count);

            foreach (SellItemState state in sis)
            {
                m_Stream.Write((int)state.Item.Serial);
                m_Stream.Write((ushort)state.Item.ItemID);
                m_Stream.Write((ushort)state.Item.Hue);
                m_Stream.Write((ushort)state.Item.Amount);
                m_Stream.Write((ushort)state.Price);

                string name = state.Item.Name;

                if (name == null || (name = name.Trim()).Length <= 0)
                    name = state.Name;

                if (name == null)
                    name = "";

                m_Stream.Write((ushort)(name.Length));
                m_Stream.WriteAsciiFixed(name, (ushort)(name.Length));
            }
        }
    }

    public sealed class EndVendorSell : Packet
    {
        public EndVendorSell(Mobile Vendor) : base(0x3B, 8)
        {
            m_Stream.Write((ushort)8);//length
            m_Stream.Write((int)Vendor.Serial);
            m_Stream.Write((byte)0);
        }
    }

    public sealed class EndVendorBuy : Packet
    {
        public EndVendorBuy(Mobile Vendor) : base(0x3B, 8)
        {
            m_Stream.Write((ushort)8);//length
            m_Stream.Write((int)Vendor.Serial);
            m_Stream.Write((byte)0);
        }
    }

    public sealed class DeathAnimation : Packet
    {
        public DeathAnimation(Mobile killed, Item corpse) : base(0xAF, 13)
        {
            m_Stream.Write((int)killed.Serial);
            m_Stream.Write((int)(corpse == null ? Serial.Zero : corpse.Serial));
            m_Stream.Write((int)0);
        }
    }

    public sealed class StatLockInfo : Packet
    {
        public StatLockInfo(Mobile m) : base(0xBF)
        {
            this.EnsureCapacity(12);

            m_Stream.Write((short)0x19);
            m_Stream.Write((byte)2);
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((byte)0);

            int lockBits = 0;

            lockBits |= (int)m.StrLock << 4;
            lockBits |= (int)m.DexLock << 2;
            lockBits |= (int)m.IntLock;

            m_Stream.Write((byte)lockBits);
        }
    }

    public class EquipInfoAttribute
    {
        private int m_Number;
        private int m_Charges;

        public int Number
        {
            get
            {
                return m_Number;
            }
        }

        public int Charges
        {
            get
            {
                return m_Charges;
            }
        }

        public EquipInfoAttribute(int number) : this(number, -1)
        {
        }

        public EquipInfoAttribute(int number, int charges)
        {
            m_Number = number;
            m_Charges = charges;
        }
    }

    public class EquipmentInfo
    {
        private int m_Number;
        private Mobile m_Crafter;
        private bool m_Unidentified;
        private EquipInfoAttribute[] m_Attributes;

        public int Number
        {
            get
            {
                return m_Number;
            }
        }

        public Mobile Crafter
        {
            get
            {
                return m_Crafter;
            }
        }

        public bool Unidentified
        {
            get
            {
                return m_Unidentified;
            }
        }

        public EquipInfoAttribute[] Attributes
        {
            get
            {
                return m_Attributes;
            }
        }

        public EquipmentInfo(int number, Mobile crafter, bool unidentified, EquipInfoAttribute[] attributes)
        {
            m_Number = number;
            m_Crafter = crafter;
            m_Unidentified = unidentified;
            m_Attributes = attributes;
        }
    }

    public sealed class DisplayEquipmentInfo : Packet
    {
        public DisplayEquipmentInfo(Item item, EquipmentInfo info) : base(0xBF)
        {
            EquipInfoAttribute[] attrs = info.Attributes;

            this.EnsureCapacity(17 + (info.Crafter == null ? 0 : 6 + info.Crafter.Name == null ? 0 : info.Crafter.Name.Length) + (info.Unidentified ? 4 : 0) + (attrs.Length * 6));

            m_Stream.Write((short)0x10);
            m_Stream.Write((int)item.Serial);

            m_Stream.Write((int)info.Number);

            if (info.Crafter != null)
            {
                string name = info.Crafter.Name;

                m_Stream.Write((int)-3);

                if (name == null)
                    m_Stream.Write((ushort)0);
                else
                {
                    int length = name.Length;
                    m_Stream.Write((ushort)length);
                    m_Stream.WriteAsciiFixed(name, length);
                }
            }

            if (info.Unidentified)
            {
                m_Stream.Write((int)-4);
            }

            for (int i = 0; i < attrs.Length; ++i)
            {
                m_Stream.Write((int)attrs[i].Number);
                m_Stream.Write((short)attrs[i].Charges);
            }

            m_Stream.Write((int)-1);
        }
    }

    public sealed class ChangeUpdateRange : Packet
    {
        private static ChangeUpdateRange[] m_Cache = new ChangeUpdateRange[0x100];

        public static ChangeUpdateRange Instantiate(int range)
        {
            byte idx = (byte)range;
            ChangeUpdateRange p = m_Cache[idx];

            if (p == null)
            {
                m_Cache[idx] = p = new ChangeUpdateRange(range);
                p.SetStatic();
            }

            return p;
        }

        public ChangeUpdateRange(int range) : base(0xC8, 2)
        {
            m_Stream.Write((byte)range);
        }
    }

    public sealed class ChangeCombatant : Packet
    {
        public ChangeCombatant(Mobile combatant) : base(0xAA, 5)
        {
            m_Stream.Write(combatant != null ? combatant.Serial : Serial.Zero);
        }
    }

    public sealed class DisplayHuePicker : Packet
    {
        public DisplayHuePicker(HuePicker huePicker) : base(0x95, 9)
        {
            m_Stream.Write((int)huePicker.Serial);
            m_Stream.Write((short)0);
            m_Stream.Write((short)huePicker.ItemID);
        }
    }

    public sealed class TripTimeResponse : Packet
    {
        public TripTimeResponse(int unk) : base(0xC9, 6)
        {
            m_Stream.Write((byte)unk);
            m_Stream.Write((int)Environment.TickCount);
        }
    }

    public sealed class UTripTimeResponse : Packet
    {
        public UTripTimeResponse(int unk) : base(0xCA, 6)
        {
            m_Stream.Write((byte)unk);
            m_Stream.Write((int)Environment.TickCount);
        }
    }

    public sealed class UnicodePrompt : Packet
    {
        public UnicodePrompt(Prompt prompt) : base(0xC2)
        {
            this.EnsureCapacity(21);

            m_Stream.Write((int)prompt.Serial);
            m_Stream.Write((int)prompt.Serial);
            m_Stream.Write((int)0);
            m_Stream.Write((int)0);
            m_Stream.Write((short)0);
        }
    }

    public sealed class ChangeCharacter : Packet
    {
        public ChangeCharacter(IAccount a) : base(0x81)
        {
            this.EnsureCapacity(305);

            int count = 0;

            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != null)
                    ++count;
            }

            m_Stream.Write((byte)count);
            m_Stream.Write((byte)0);

            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != null)
                {
                    string name = a[i].Name;

                    if (name == null)
                        name = "-null-";
                    else if ((name = name.Trim()).Length == 0)
                        name = "-empty-";

                    m_Stream.WriteAsciiFixed(name, 30);
                    m_Stream.Fill(30); // password
                }
                else
                {
                    m_Stream.Fill(60);
                }
            }
        }
    }

    public sealed class DeathStatus : Packet
    {
        public static readonly Packet Dead = Packet.SetStatic(new DeathStatus(true));
        public static readonly Packet Alive = Packet.SetStatic(new DeathStatus(false));

        public static Packet Instantiate(bool dead)
        {
            return (dead ? Dead : Alive);
        }

        public DeathStatus(bool dead) : base(0x2C, 2)
        {
            m_Stream.Write((byte)(dead ? 0 : 2));
        }
    }

    public sealed class SpeedControl : Packet
    {
        public static readonly Packet WalkSpeed = Packet.SetStatic(new SpeedControl(2));
        public static readonly Packet MountSpeed = Packet.SetStatic(new SpeedControl(1));
        public static readonly Packet Disable = Packet.SetStatic(new SpeedControl(0));

        public SpeedControl(int speedControl)
            : base(0xBF)
        {
            EnsureCapacity(3);

            m_Stream.Write((short)0x26);
            m_Stream.Write((byte)speedControl);
        }
    }

    public sealed class InvalidMapEnable : Packet
    {
        public InvalidMapEnable() : base(0xC6, 1)
        {
        }
    }

    public sealed class BondedStatus : Packet
    {
        public BondedStatus(int val1, Serial serial, int val2) : base(0xBF)
        {
            this.EnsureCapacity(11);

            m_Stream.Write((short)0x19);
            m_Stream.Write((byte)val1);
            m_Stream.Write((int)serial);
            m_Stream.Write((byte)val2);
        }
    }

    public sealed class ToggleSpecialAbility : Packet
    {
        public ToggleSpecialAbility(int abilityID, bool active)
            : base(0xBF)
        {
            EnsureCapacity(7);

            m_Stream.Write((short)0x25);

            m_Stream.Write((short)abilityID);
            m_Stream.Write((bool)active);
        }
    }

    public sealed class DisplayItemListMenu : Packet
    {
        public DisplayItemListMenu(ItemListMenu menu) : base(0x7C)
        {
            this.EnsureCapacity(256);

            m_Stream.Write((int)((IMenu)menu).Serial);
            m_Stream.Write((short)0);

            string question = menu.Question;

            if (question == null)
                m_Stream.Write((byte)0);
            else
            {
                int questionLength = question.Length;
                m_Stream.Write((byte)questionLength);
                m_Stream.WriteAsciiFixed(question, questionLength);
            }

            ItemListEntry[] entries = menu.Entries;

            int entriesLength = (byte)entries.Length;

            m_Stream.Write((byte)entriesLength);

            for (int i = 0; i < entriesLength; ++i)
            {
                ItemListEntry e = entries[i];

                m_Stream.Write((ushort)e.ItemID);
                m_Stream.Write((short)e.Hue);

                string name = e.Name;

                if (name == null)
                    m_Stream.Write((byte)0);
                else
                {
                    int nameLength = name.Length;
                    m_Stream.Write((byte)nameLength);
                    m_Stream.WriteAsciiFixed(name, nameLength);
                }
            }
        }
    }

    public sealed class DisplayQuestionMenu : Packet
    {
        public DisplayQuestionMenu(QuestionMenu menu) : base(0x7C)
        {
            this.EnsureCapacity(256);

            m_Stream.Write((int)((IMenu)menu).Serial);
            m_Stream.Write((short)0);

            string question = menu.Question;

            if (question == null)
                m_Stream.Write((byte)0);
            else
            {
                int questionLength = question.Length;
                m_Stream.Write((byte)questionLength);
                m_Stream.WriteAsciiFixed(question, questionLength);
            }

            string[] answers = menu.Answers;

            int answersLength = (byte)answers.Length;

            m_Stream.Write((byte)answersLength);

            for (int i = 0; i < answersLength; ++i)
            {
                m_Stream.Write((int)0);

                string answer = answers[i];

                if (answer == null)
                    m_Stream.Write((byte)0);
                else
                {
                    int answerLength = answer.Length;
                    m_Stream.Write((byte)answerLength);
                    m_Stream.WriteAsciiFixed(answer, answerLength);
                }
            }
        }
    }

    public sealed class GlobalLightLevel : Packet
    {
        private static GlobalLightLevel[] m_Cache = new GlobalLightLevel[0x100];

        public static GlobalLightLevel Instantiate(int level)
        {
            byte lvl = (byte)level;
            GlobalLightLevel p = m_Cache[lvl];

            if (p == null)
            {
                m_Cache[lvl] = p = new GlobalLightLevel(level);
                p.SetStatic();
            }

            return p;
        }

        public GlobalLightLevel(int level) : base(0x4F, 2)
        {
            m_Stream.Write((sbyte)level);
        }
    }

    public sealed class PersonalLightLevel : Packet
    {
        public PersonalLightLevel(Mobile m) : this(m, m.LightLevel)
        {
        }

        public PersonalLightLevel(Mobile m, int level) : base(0x4E, 6)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((sbyte)level);
        }
    }

    public sealed class PersonalLightLevelZero : Packet
    {
        public PersonalLightLevelZero(Mobile m) : base(0x4E, 6)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((sbyte)0);
        }
    }

    public enum CMEFlags
    {
        None = 0x00,
        Disabled = 0x01,
        Arrow = 0x02,
        Highlighted = 0x04,
        Colored = 0x20
    }

    public sealed class DisplayContextMenu : Packet
    {
        public DisplayContextMenu(ContextMenu menu) : base(0xBF)
        {
            ContextMenuEntry[] entries = menu.Entries;

            int length = (byte)entries.Length;

            this.EnsureCapacity(12 + (length * 8));

            m_Stream.Write((short)0x14);
            m_Stream.Write((short)0x02);

            IEntity target = menu.Target as IEntity;

            m_Stream.Write((int)(target == null ? Serial.MinusOne : target.Serial));

            m_Stream.Write((byte)length);

            Point3D p;

            if (target is Mobile)
                p = target.Location;
            else if (target is Item)
                p = ((Item)target).GetWorldLocation();
            else
                p = Point3D.Zero;

            for (int i = 0; i < length; ++i)
            {
                ContextMenuEntry e = entries[i];

                m_Stream.Write((int)e.Number);
                m_Stream.Write((short)i);

                int range = e.Range;

                if (range == -1)
                    range = 18;

                CMEFlags flags = (e.Enabled && menu.From.InRange(p, range)) ? CMEFlags.None : CMEFlags.Disabled;

                flags |= e.Flags;

                m_Stream.Write((short)flags);
            }
        }
    }

    public sealed class DisplayContextMenuOld : Packet
    {
        public DisplayContextMenuOld(ContextMenu menu) : base(0xBF)
        {
            ContextMenuEntry[] entries = menu.Entries;

            int length = (byte)entries.Length;

            this.EnsureCapacity(12 + (length * 8));

            m_Stream.Write((short)0x14);
            m_Stream.Write((short)0x01);

            IEntity target = menu.Target as IEntity;

            m_Stream.Write((int)(target == null ? Serial.MinusOne : target.Serial));

            m_Stream.Write((byte)length);

            Point3D p;

            if (target is Mobile)
                p = target.Location;
            else if (target is Item)
                p = ((Item)target).GetWorldLocation();
            else
                p = Point3D.Zero;

            for (int i = 0; i < length; ++i)
            {
                ContextMenuEntry e = entries[i];

                m_Stream.Write((short)i);
                m_Stream.Write((ushort)(e.Number - 3000000));

                int range = e.Range;

                if (range == -1)
                    range = 18;

                CMEFlags flags = (e.Enabled && menu.From.InRange(p, range)) ? CMEFlags.None : CMEFlags.Disabled;

                int color = e.Color & 0xFFFF;

                if (color != 0xFFFF)
                    flags |= CMEFlags.Colored;

                flags |= e.Flags;

                m_Stream.Write((short)flags);

                if ((flags & CMEFlags.Colored) != 0)
                    m_Stream.Write((short)color);
            }
        }
    }

    public sealed class DisplayProfile : Packet
    {
        public DisplayProfile(bool realSerial, Mobile m, string header, string body, string footer) : base(0xB8)
        {
            if (header == null)
                header = "";

            if (body == null)
                body = "";

            if (footer == null)
                footer = "";

            EnsureCapacity(12 + header.Length + (footer.Length * 2) + (body.Length * 2));

            m_Stream.Write((int)(realSerial ? m.Serial : Serial.Zero));
            m_Stream.WriteAsciiNull(header);
            m_Stream.WriteBigUniNull(footer);
            m_Stream.WriteBigUniNull(body);
        }
    }

    public sealed class CloseGump : Packet
    {
        public CloseGump(int typeID, int buttonID) : base(0xBF)
        {
            this.EnsureCapacity(13);

            m_Stream.Write((short)0x04);
            m_Stream.Write((int)typeID);
            m_Stream.Write((int)buttonID);
        }
    }

    public sealed class EquipUpdate : Packet
    {
        public EquipUpdate(Item item) : base(0x2E, 15)
        {
            Serial parentSerial;

            if (item.Parent is Mobile)
            {
                parentSerial = ((Mobile)item.Parent).Serial;
            }
            else
            {
                Console.WriteLine("Warning: EquipUpdate on item with !(parent is Mobile)");
                parentSerial = Serial.Zero;
            }

            int hue = item.Hue;

            if (item.Parent is Mobile)
            {
                Mobile mob = (Mobile)item.Parent;

                if (mob.SolidHueOverride >= 0)
                    hue = mob.SolidHueOverride;
            }

            m_Stream.Write((int)item.Serial);
            m_Stream.Write((short)item.ItemID);
            m_Stream.Write((byte)0);
            m_Stream.Write((byte)item.Layer);
            m_Stream.Write((int)parentSerial);
            m_Stream.Write((short)hue);
        }
    }

    public sealed class WorldItem : Packet
    {
        public WorldItem(Item item) : base(0x1A)
        {
            this.EnsureCapacity(20);

            // 14 base length
            // +2 - Amount
            // +2 - Hue
            // +1 - Flags

            uint serial = (uint)item.Serial.Value;
            int itemID = item.ItemID & 0x3FFF;
            int amount = item.Amount;
            Point3D loc = item.Location;
            int x = loc.m_X;
            int y = loc.m_Y;
            int hue = item.Hue;
            int flags = item.GetPacketFlags();
            int direction = (int)item.Direction;

            if (amount != 0)
            {
                serial |= 0x80000000;
            }
            else
            {
                serial &= 0x7FFFFFFF;
            }

            m_Stream.Write((uint)serial);

            if (item is BaseMulti)
                m_Stream.Write((short)(itemID | 0x4000));
            else
                m_Stream.Write((short)itemID);

            if (amount != 0)
            {
                m_Stream.Write((short)amount);
            }

            x &= 0x7FFF;

            if (direction != 0)
            {
                x |= 0x8000;
            }

            m_Stream.Write((short)x);

            y &= 0x3FFF;

            if (hue != 0)
            {
                y |= 0x8000;
            }

            if (flags != 0)
            {
                y |= 0x4000;
            }

            m_Stream.Write((short)y);

            if (direction != 0)
                m_Stream.Write((byte)direction);

            m_Stream.Write((sbyte)loc.m_Z);

            if (hue != 0)
                m_Stream.Write((ushort)hue);

            if (flags != 0)
                m_Stream.Write((byte)flags);
        }
    }

    public sealed class WorldItemSA : Packet
    {
        public WorldItemSA(Item item) : base(0xF3, 24)
        {
            m_Stream.Write((short)0x1);

            int itemID = item.ItemID;

            if (item is BaseMulti)
            {
                m_Stream.Write((byte)0x02);

                m_Stream.Write((int)item.Serial);

                itemID &= 0x3FFF;

                m_Stream.Write((short)itemID);

                m_Stream.Write((byte)0);
                /*} else if (  ) {
                    m_Stream.Write( (byte) 0x01 );

                    m_Stream.Write( (int) item.Serial );

                    m_Stream.Write( (short) itemID ); 

                    m_Stream.Write( (byte) item.Direction );*/
            }
            else
            {
                m_Stream.Write((byte)0x00);

                m_Stream.Write((int)item.Serial);

                itemID &= 0x7FFF;

                m_Stream.Write((short)itemID);

                m_Stream.Write((byte)0);
            }

            int amount = item.Amount;
            m_Stream.Write((short)amount);
            m_Stream.Write((short)amount);

            Point3D loc = item.Location;
            int x = loc.m_X & 0x7FFF;
            int y = loc.m_Y & 0x3FFF;
            m_Stream.Write((short)x);
            m_Stream.Write((short)y);
            m_Stream.Write((sbyte)loc.m_Z);

            m_Stream.Write((byte)item.Light);
            m_Stream.Write((short)item.Hue);
            m_Stream.Write((byte)item.GetPacketFlags());
        }
    }

    public sealed class WorldItemHS : Packet
    {
        public WorldItemHS(Item item) : base(0xF3, 26)
        {
            m_Stream.Write((short)0x1);

            int itemID = item.ItemID;

            if (item is BaseMulti)
            {
                m_Stream.Write((byte)0x02);

                m_Stream.Write((int)item.Serial);

                itemID &= 0x3FFF;

                m_Stream.Write((ushort)itemID);

                m_Stream.Write((byte)0);
                /*} else if (  ) {
                    m_Stream.Write( (byte) 0x01 );

                    m_Stream.Write( (int) item.Serial );

                    m_Stream.Write( (ushort) itemID ); 

                    m_Stream.Write( (byte) item.Direction );*/
            }
            else
            {
                m_Stream.Write((byte)0x00);

                m_Stream.Write((int)item.Serial);

                itemID &= 0xFFFF;

                m_Stream.Write((ushort)itemID);

                m_Stream.Write((byte)0);
            }

            int amount = item.Amount;
            m_Stream.Write((short)amount);
            m_Stream.Write((short)amount);

            Point3D loc = item.Location;
            int x = loc.m_X & 0x7FFF;
            int y = loc.m_Y & 0x3FFF;
            m_Stream.Write((short)x);
            m_Stream.Write((short)y);
            m_Stream.Write((sbyte)loc.m_Z);

            m_Stream.Write((byte)item.Light);
            m_Stream.Write((short)item.Hue);
            m_Stream.Write((byte)item.GetPacketFlags());

            m_Stream.Write((short)0x00); // ??
        }
    }

    public sealed class LiftRej : Packet
    {
        public LiftRej(LRReason reason) : base(0x27, 2)
        {
            m_Stream.Write((byte)reason);
        }
    }

    public sealed class LogoutAck : Packet
    {
        public LogoutAck() : base(0xD1, 2)
        {
            m_Stream.Write((byte)0x01);
        }
    }

    public sealed class Weather : Packet
    {
        public Weather(int v1, int v2, int v3) : base(0x65, 4)
        {
            m_Stream.Write((byte)v1);
            m_Stream.Write((byte)v2);
            m_Stream.Write((byte)v3);
        }
    }

    public sealed class UnkD3 : Packet
    {
        public UnkD3(Mobile beholder, Mobile beheld) : base(0xD3)
        {
            this.EnsureCapacity(256);

            //int
            //short
            //short
            //short
            //byte
            //byte
            //short
            //byte
            //byte
            //short
            //short
            //short
            //while ( int != 0 )
            //{
            //short
            //byte
            //short
            //}

            m_Stream.Write((int)beheld.Serial);
            m_Stream.Write((short)beheld.Body);
            m_Stream.Write((short)beheld.X);
            m_Stream.Write((short)beheld.Y);
            m_Stream.Write((sbyte)beheld.Z);
            m_Stream.Write((byte)beheld.Direction);
            m_Stream.Write((ushort)beheld.Hue);
            m_Stream.Write((byte)beheld.GetPacketFlags());
            m_Stream.Write((byte)Notoriety.Compute(beholder, beheld));

            m_Stream.Write((short)0);
            m_Stream.Write((short)0);
            m_Stream.Write((short)0);

            m_Stream.Write((int)0);
        }
    }

    public sealed class GQRequest : Packet
    {
        public GQRequest() : base(0xC3)
        {
            this.EnsureCapacity(256);

            m_Stream.Write((int)1);
            m_Stream.Write((int)2); // ID
            m_Stream.Write((int)3); // Customer ? (this)
            m_Stream.Write((int)4); // Customer this (?)
            m_Stream.Write((int)0);
            m_Stream.Write((short)0);
            m_Stream.Write((short)6);
            m_Stream.Write((byte)'r');
            m_Stream.Write((byte)'e');
            m_Stream.Write((byte)'g');
            m_Stream.Write((byte)'i');
            m_Stream.Write((byte)'o');
            m_Stream.Write((byte)'n');
            m_Stream.Write((int)7); // Call time in seconds
            m_Stream.Write((short)2); // Map (0=fel,1=tram,2=ilsh)
            m_Stream.Write((int)8); // X
            m_Stream.Write((int)9); // Y
            m_Stream.Write((int)10); // Z
            m_Stream.Write((int)11); // Volume
            m_Stream.Write((int)12); // Rank
            m_Stream.Write((int)-1);
            m_Stream.Write((int)1); // type
        }
    }

    /// <summary>
    /// Causes the client to walk in a given direction. It does not send a movement request.
    /// </summary>
    public sealed class PlayerMove : Packet
    {
        public PlayerMove(Direction d) : base(0x97, 2)
        {
            m_Stream.Write((byte)d);

            // @4C63B0
        }
    }

    /// <summary>
    /// Displays a message "There are currently [count] available calls in the global queue.".
    /// </summary>
    public sealed class GQCount : Packet
    {
        public GQCount(int unk, int count) : base(0xCB, 7)
        {
            m_Stream.Write((short)unk);
            m_Stream.Write((int)count);
        }
    }

    /// <summary>
    /// Asks the client for it's version
    /// </summary>
    public sealed class ClientVersionReq : Packet
    {
        public ClientVersionReq() : base(0xBD)
        {
            this.EnsureCapacity(3);
        }
    }

    /// <summary>
    /// Asks the client for it's "assist version". (Perhaps for UOAssist?)
    /// </summary>
    public sealed class AssistVersionReq : Packet
    {
        public AssistVersionReq(int unk) : base(0xBE)
        {
            this.EnsureCapacity(7);

            m_Stream.Write((int)unk);
        }
    }

    public enum EffectType
    {
        Moving = 0x00,
        Lightning = 0x01,
        FixedXYZ = 0x02,
        FixedFrom = 0x03
    }

    public class ParticleEffect : Packet
    {
        public ParticleEffect(EffectType type, Serial from, Serial to, int itemID, Point3D fromPoint, Point3D toPoint, int speed, int duration, bool fixedDirection, bool explode, int hue, int renderMode, int effect, int explodeEffect, int explodeSound, Serial serial, int layer, int unknown) : base(0xC7, 49)
        {
            m_Stream.Write((byte)type);
            m_Stream.Write((int)from);
            m_Stream.Write((int)to);
            m_Stream.Write((short)itemID);
            m_Stream.Write((short)fromPoint.m_X);
            m_Stream.Write((short)fromPoint.m_Y);
            m_Stream.Write((sbyte)fromPoint.m_Z);
            m_Stream.Write((short)toPoint.m_X);
            m_Stream.Write((short)toPoint.m_Y);
            m_Stream.Write((sbyte)toPoint.m_Z);
            m_Stream.Write((byte)speed);
            m_Stream.Write((byte)duration);
            m_Stream.Write((byte)0);
            m_Stream.Write((byte)0);
            m_Stream.Write((bool)fixedDirection);
            m_Stream.Write((bool)explode);
            m_Stream.Write((int)hue);
            m_Stream.Write((int)renderMode);
            m_Stream.Write((short)effect);
            m_Stream.Write((short)explodeEffect);
            m_Stream.Write((short)explodeSound);
            m_Stream.Write((int)serial);
            m_Stream.Write((byte)layer);
            m_Stream.Write((short)unknown);
        }

        public ParticleEffect(EffectType type, Serial from, Serial to, int itemID, IPoint3D fromPoint, IPoint3D toPoint, int speed, int duration, bool fixedDirection, bool explode, int hue, int renderMode, int effect, int explodeEffect, int explodeSound, Serial serial, int layer, int unknown) : base(0xC7, 49)
        {
            m_Stream.Write((byte)type);
            m_Stream.Write((int)from);
            m_Stream.Write((int)to);
            m_Stream.Write((short)itemID);
            m_Stream.Write((short)fromPoint.X);
            m_Stream.Write((short)fromPoint.Y);
            m_Stream.Write((sbyte)fromPoint.Z);
            m_Stream.Write((short)toPoint.X);
            m_Stream.Write((short)toPoint.Y);
            m_Stream.Write((sbyte)toPoint.Z);
            m_Stream.Write((byte)speed);
            m_Stream.Write((byte)duration);
            m_Stream.Write((byte)0);
            m_Stream.Write((byte)0);
            m_Stream.Write((bool)fixedDirection);
            m_Stream.Write((bool)explode);
            m_Stream.Write((int)hue);
            m_Stream.Write((int)renderMode);
            m_Stream.Write((short)effect);
            m_Stream.Write((short)explodeEffect);
            m_Stream.Write((short)explodeSound);
            m_Stream.Write((int)serial);
            m_Stream.Write((byte)layer);
            m_Stream.Write((short)unknown);
        }
    }

    public class HuedEffect : Packet
    {
        public HuedEffect(EffectType type, Serial from, Serial to, int itemID, Point3D fromPoint, Point3D toPoint, int speed, int duration, bool fixedDirection, bool explode, int hue, int renderMode) : base(0xC0, 36)
        {
            m_Stream.Write((byte)type);
            m_Stream.Write((int)from);
            m_Stream.Write((int)to);
            m_Stream.Write((short)itemID);
            m_Stream.Write((short)fromPoint.m_X);
            m_Stream.Write((short)fromPoint.m_Y);
            m_Stream.Write((sbyte)fromPoint.m_Z);
            m_Stream.Write((short)toPoint.m_X);
            m_Stream.Write((short)toPoint.m_Y);
            m_Stream.Write((sbyte)toPoint.m_Z);
            m_Stream.Write((byte)speed);
            m_Stream.Write((byte)duration);
            m_Stream.Write((byte)0);
            m_Stream.Write((byte)0);
            m_Stream.Write((bool)fixedDirection);
            m_Stream.Write((bool)explode);
            m_Stream.Write((int)hue);
            m_Stream.Write((int)renderMode);
        }

        public HuedEffect(EffectType type, Serial from, Serial to, int itemID, IPoint3D fromPoint, IPoint3D toPoint, int speed, int duration, bool fixedDirection, bool explode, int hue, int renderMode) : base(0xC0, 36)
        {
            m_Stream.Write((byte)type);
            m_Stream.Write((int)from);
            m_Stream.Write((int)to);
            m_Stream.Write((short)itemID);
            m_Stream.Write((short)fromPoint.X);
            m_Stream.Write((short)fromPoint.Y);
            m_Stream.Write((sbyte)fromPoint.Z);
            m_Stream.Write((short)toPoint.X);
            m_Stream.Write((short)toPoint.Y);
            m_Stream.Write((sbyte)toPoint.Z);
            m_Stream.Write((byte)speed);
            m_Stream.Write((byte)duration);
            m_Stream.Write((byte)0);
            m_Stream.Write((byte)0);
            m_Stream.Write((bool)fixedDirection);
            m_Stream.Write((bool)explode);
            m_Stream.Write((int)hue);
            m_Stream.Write((int)renderMode);
        }
    }

    public sealed class TargetParticleEffect : ParticleEffect
    {
        public TargetParticleEffect(IEntity e, int itemID, int speed, int duration, int hue, int renderMode, int effect, int layer, int unknown) : base(EffectType.FixedFrom, e.Serial, Serial.Zero, itemID, e.Location, e.Location, speed, duration, true, false, hue, renderMode, effect, 1, 0, e.Serial, layer, unknown)
        {
        }
    }

    public sealed class TargetEffect : HuedEffect
    {
        public TargetEffect(IEntity e, int itemID, int speed, int duration, int hue, int renderMode) : base(EffectType.FixedFrom, e.Serial, Serial.Zero, itemID, e.Location, e.Location, speed, duration, true, false, hue, renderMode)
        {
        }
    }

    public sealed class LocationParticleEffect : ParticleEffect
    {
        public LocationParticleEffect(IEntity e, int itemID, int speed, int duration, int hue, int renderMode, int effect, int unknown) : base(EffectType.FixedXYZ, e.Serial, Serial.Zero, itemID, e.Location, e.Location, speed, duration, true, false, hue, renderMode, effect, 1, 0, e.Serial, 255, unknown)
        {
        }
    }

    public sealed class LocationEffect : HuedEffect
    {
        public LocationEffect(IPoint3D p, int itemID, int speed, int duration, int hue, int renderMode) : base(EffectType.FixedXYZ, Serial.Zero, Serial.Zero, itemID, p, p, speed, duration, true, false, hue, renderMode)
        {
        }
    }

    public sealed class MovingParticleEffect : ParticleEffect
    {
        public MovingParticleEffect(IEntity from, IEntity to, int itemID, int speed, int duration, bool fixedDirection, bool explodes, int hue, int renderMode, int effect, int explodeEffect, int explodeSound, EffectLayer layer, int unknown) : base(EffectType.Moving, from.Serial, to.Serial, itemID, from.Location, to.Location, speed, duration, fixedDirection, explodes, hue, renderMode, effect, explodeEffect, explodeSound, Serial.Zero, (int)layer, unknown)
        {
        }
    }

    public sealed class MovingEffect : HuedEffect
    {
        public MovingEffect(IEntity from, IEntity to, int itemID, int speed, int duration, bool fixedDirection, bool explodes, int hue, int renderMode) : base(EffectType.Moving, from.Serial, to.Serial, itemID, from.Location, to.Location, speed, duration, fixedDirection, explodes, hue, renderMode)
        {
        }
    }

    public enum ScreenEffectType
    {
        FadeOut = 0x00,
        FadeIn = 0x01,
        LightFlash = 0x02,
        FadeInOut = 0x03,
        DarkFlash = 0x04
    }

    public class ScreenEffect : Packet
    {
        public ScreenEffect(ScreenEffectType type)
            : base(0x70, 28)
        {
            m_Stream.Write((byte)0x04);
            m_Stream.Fill(8);
            m_Stream.Write((short)type);
            m_Stream.Fill(16);
        }
    }

    public sealed class ScreenFadeOut : ScreenEffect
    {
        public static readonly Packet Instance = Packet.SetStatic(new ScreenFadeOut());

        public ScreenFadeOut()
            : base(ScreenEffectType.FadeOut)
        {
        }
    }

    public sealed class ScreenFadeIn : ScreenEffect
    {
        public static readonly Packet Instance = Packet.SetStatic(new ScreenFadeIn());

        public ScreenFadeIn()
            : base(ScreenEffectType.FadeIn)
        {
        }
    }

    public sealed class ScreenFadeInOut : ScreenEffect
    {
        public static readonly Packet Instance = Packet.SetStatic(new ScreenFadeInOut());

        public ScreenFadeInOut()
            : base(ScreenEffectType.FadeInOut)
        {
        }
    }

    public sealed class ScreenLightFlash : ScreenEffect
    {
        public static readonly Packet Instance = Packet.SetStatic(new ScreenLightFlash());

        public ScreenLightFlash()
            : base(ScreenEffectType.LightFlash)
        {
        }
    }

    public sealed class ScreenDarkFlash : ScreenEffect
    {
        public static readonly Packet Instance = Packet.SetStatic(new ScreenDarkFlash());

        public ScreenDarkFlash()
            : base(ScreenEffectType.DarkFlash)
        {
        }
    }

    public enum DeleteResultType
    {
        PasswordInvalid,
        CharNotExist,
        CharBeingPlayed,
        CharTooYoung,
        CharQueued,
        BadRequest
    }

    public sealed class DeleteResult : Packet
    {
        public DeleteResult(DeleteResultType res) : base(0x85, 2)
        {
            m_Stream.Write((byte)res);
        }
    }

    /*public sealed class MovingEffect : Packet
	{
		public MovingEffect( IEntity from, IEntity to, int itemID, int speed, int duration, bool fixedDirection, bool turn, int hue, int renderMode ) : base( 0xC0, 36 )
		{
			m_Stream.Write( (byte) 0x00 );
			m_Stream.Write( (int) from.Serial );
			m_Stream.Write( (int) to.Serial );
			m_Stream.Write( (short) itemID );
			m_Stream.Write( (short) from.Location.m_X );
			m_Stream.Write( (short) from.Location.m_Y );
			m_Stream.Write( (sbyte) from.Location.m_Z );
			m_Stream.Write( (short) to.Location.m_X );
			m_Stream.Write( (short) to.Location.m_Y );
			m_Stream.Write( (sbyte) to.Location.m_Z );
			m_Stream.Write( (byte) speed );
			m_Stream.Write( (byte) duration );
			m_Stream.Write( (byte) 0 );
			m_Stream.Write( (byte) 0 );
			m_Stream.Write( (bool) fixedDirection );
			m_Stream.Write( (bool) turn );
			m_Stream.Write( (int) hue );
			m_Stream.Write( (int) renderMode );
		}
	}*/

    /*public sealed class LocationEffect : Packet
	{
		public LocationEffect( IPoint3D p, int itemID, int duration, int hue, int renderMode ) : base( 0xC0, 36 )
		{
			m_Stream.Write( (byte) 0x02 );
			m_Stream.Write( (int) Serial.Zero );
			m_Stream.Write( (int) Serial.Zero );
			m_Stream.Write( (short) itemID );
			m_Stream.Write( (short) p.X );
			m_Stream.Write( (short) p.Y );
			m_Stream.Write( (sbyte) p.Z );
			m_Stream.Write( (short) p.X );
			m_Stream.Write( (short) p.Y );
			m_Stream.Write( (sbyte) p.Z );
			m_Stream.Write( (byte) 10 );
			m_Stream.Write( (byte) duration );
			m_Stream.Write( (byte) 0 );
			m_Stream.Write( (byte) 0 );
			m_Stream.Write( (byte) 1 );
			m_Stream.Write( (byte) 0 );
			m_Stream.Write( (int) hue );
			m_Stream.Write( (int) renderMode );
		}
	}*/

    public sealed class BoltEffect : Packet
    {
        public BoltEffect(IEntity target, int hue) : base(0xC0, 36)
        {
            m_Stream.Write((byte)0x01); // type
            m_Stream.Write((int)target.Serial);
            m_Stream.Write((int)Serial.Zero);
            m_Stream.Write((short)0); // itemID
            m_Stream.Write((short)target.X);
            m_Stream.Write((short)target.Y);
            m_Stream.Write((sbyte)target.Z);
            m_Stream.Write((short)target.X);
            m_Stream.Write((short)target.Y);
            m_Stream.Write((sbyte)target.Z);
            m_Stream.Write((byte)0); // speed
            m_Stream.Write((byte)0); // duration
            m_Stream.Write((short)0); // unk
            m_Stream.Write(false); // fixed direction
            m_Stream.Write(false); // explode
            m_Stream.Write((int)hue);
            m_Stream.Write((int)0); // render mode
        }
    }

    public sealed class DisplaySpellbook : Packet
    {
        public DisplaySpellbook(Item book) : base(0x24, 7)
        {
            m_Stream.Write((int)book.Serial);
            m_Stream.Write((short)-1);
        }
    }

    public sealed class DisplaySpellbookHS : Packet
    {
        public DisplaySpellbookHS(Item book) : base(0x24, 9)
        {
            m_Stream.Write((int)book.Serial);
            m_Stream.Write((short)-1);
            m_Stream.Write((short)0x7D);
        }
    }

    public sealed class NewSpellbookContent : Packet
    {
        public NewSpellbookContent(Item item, int graphic, int offset, ulong content) : base(0xBF)
        {
            EnsureCapacity(23);

            m_Stream.Write((short)0x1B);
            m_Stream.Write((short)0x01);

            m_Stream.Write((int)item.Serial);
            m_Stream.Write((short)graphic);
            m_Stream.Write((short)offset);

            for (int i = 0; i < 8; ++i)
                m_Stream.Write((byte)(content >> (i * 8)));
        }
    }

    public sealed class SpellbookContent : Packet
    {
        public SpellbookContent(int count, int offset, ulong content, Item item) : base(0x3C)
        {
            this.EnsureCapacity(5 + (count * 19));

            int written = 0;

            m_Stream.Write((ushort)0);

            ulong mask = 1;

            for (int i = 0; i < 64; ++i, mask <<= 1)
            {
                if ((content & mask) != 0)
                {
                    m_Stream.Write((int)(0x7FFFFFFF - i));
                    m_Stream.Write((ushort)0);
                    m_Stream.Write((byte)0);
                    m_Stream.Write((ushort)(i + offset));
                    m_Stream.Write((short)0);
                    m_Stream.Write((short)0);
                    m_Stream.Write((int)item.Serial);
                    m_Stream.Write((short)0);

                    ++written;
                }
            }

            m_Stream.Seek(3, SeekOrigin.Begin);
            m_Stream.Write((ushort)written);
        }
    }

    public sealed class SpellbookContent6017 : Packet
    {
        public SpellbookContent6017(int count, int offset, ulong content, Item item) : base(0x3C)
        {
            this.EnsureCapacity(5 + (count * 20));

            int written = 0;

            m_Stream.Write((ushort)0);

            ulong mask = 1;

            for (int i = 0; i < 64; ++i, mask <<= 1)
            {
                if ((content & mask) != 0)
                {
                    m_Stream.Write((int)(0x7FFFFFFF - i));
                    m_Stream.Write((ushort)0);
                    m_Stream.Write((byte)0);
                    m_Stream.Write((ushort)(i + offset));
                    m_Stream.Write((short)0);
                    m_Stream.Write((short)0);
                    m_Stream.Write((byte)0); // Grid Location?
                    m_Stream.Write((int)item.Serial);
                    m_Stream.Write((short)0);

                    ++written;
                }
            }

            m_Stream.Seek(3, SeekOrigin.Begin);
            m_Stream.Write((ushort)written);
        }
    }

    public sealed class ContainerDisplay : Packet
    {
        public ContainerDisplay(Container c) : base(0x24, 7)
        {
            m_Stream.Write((int)c.Serial);
            m_Stream.Write((short)c.GumpID);
        }
    }

    public sealed class ContainerDisplayHS : Packet
    {
        public ContainerDisplayHS(Container c) : base(0x24, 9)
        {
            m_Stream.Write((int)c.Serial);
            m_Stream.Write((short)c.GumpID);
            m_Stream.Write((short)0x7D);
        }
    }

    public sealed class ContainerContentUpdate : Packet
    {
        public ContainerContentUpdate(Item item) : base(0x25, 20)
        {
            Serial parentSerial;

            if (item.Parent is Item)
            {
                parentSerial = ((Item)item.Parent).Serial;
            }
            else
            {
                Console.WriteLine("Warning: ContainerContentUpdate on item with !(parent is Item)");
                parentSerial = Serial.Zero;
            }

            m_Stream.Write((int)item.Serial);
            m_Stream.Write((ushort)item.ItemID);
            m_Stream.Write((byte)0); // signed, itemID offset
            m_Stream.Write((ushort)item.Amount);
            m_Stream.Write((short)item.X);
            m_Stream.Write((short)item.Y);
            m_Stream.Write((int)parentSerial);
            m_Stream.Write((ushort)(item.QuestItem ? Item.QuestItemHue : item.Hue));
        }
    }

    public sealed class ContainerContentUpdate6017 : Packet
    {
        public ContainerContentUpdate6017(Item item) : base(0x25, 21)
        {
            Serial parentSerial;

            if (item.Parent is Item)
            {
                parentSerial = ((Item)item.Parent).Serial;
            }
            else
            {
                Console.WriteLine("Warning: ContainerContentUpdate on item with !(parent is Item)");
                parentSerial = Serial.Zero;
            }

            m_Stream.Write((int)item.Serial);
            m_Stream.Write((ushort)item.ItemID);
            m_Stream.Write((byte)0); // signed, itemID offset
            m_Stream.Write((ushort)item.Amount);
            m_Stream.Write((short)item.X);
            m_Stream.Write((short)item.Y);
            m_Stream.Write((byte)0); // Grid Location?
            m_Stream.Write((int)parentSerial);
            m_Stream.Write((ushort)(item.QuestItem ? Item.QuestItemHue : item.Hue));
        }
    }

    public sealed class ContainerContent : Packet
    {
        public ContainerContent(Mobile beholder, Item beheld) : base(0x3C)
        {
            List<Item> items = beheld.Items;
            int count = items.Count;

            this.EnsureCapacity(5 + (count * 19));

            long pos = m_Stream.Position;

            int written = 0;

            m_Stream.Write((ushort)0);

            for (int i = 0; i < count; ++i)
            {
                Item child = items[i];

                if (!child.Deleted && beholder.CanSee(child))
                {
                    Point3D loc = child.Location;

                    m_Stream.Write((int)child.Serial);
                    m_Stream.Write((ushort)child.ItemID);
                    m_Stream.Write((byte)0); // signed, itemID offset
                    m_Stream.Write((ushort)child.Amount);
                    m_Stream.Write((short)loc.m_X);
                    m_Stream.Write((short)loc.m_Y);
                    m_Stream.Write((int)beheld.Serial);
                    m_Stream.Write((ushort)(child.QuestItem ? Item.QuestItemHue : child.Hue));

                    ++written;
                }
            }

            m_Stream.Seek(pos, SeekOrigin.Begin);
            m_Stream.Write((ushort)written);
        }
    }

    public sealed class ContainerContent6017 : Packet
    {
        public ContainerContent6017(Mobile beholder, Item beheld) : base(0x3C)
        {
            List<Item> items = beheld.Items;
            int count = items.Count;

            this.EnsureCapacity(5 + (count * 20));

            long pos = m_Stream.Position;

            int written = 0;

            m_Stream.Write((ushort)0);

            for (int i = 0; i < count; ++i)
            {
                Item child = items[i];

                if (!child.Deleted && beholder.CanSee(child))
                {
                    Point3D loc = child.Location;

                    m_Stream.Write((int)child.Serial);
                    m_Stream.Write((ushort)child.ItemID);
                    m_Stream.Write((byte)0); // signed, itemID offset
                    m_Stream.Write((ushort)child.Amount);
                    m_Stream.Write((short)loc.m_X);
                    m_Stream.Write((short)loc.m_Y);
                    m_Stream.Write((byte)0); // Grid Location?
                    m_Stream.Write((int)beheld.Serial);
                    m_Stream.Write((ushort)(child.QuestItem ? Item.QuestItemHue : child.Hue));

                    ++written;
                }
            }

            m_Stream.Seek(pos, SeekOrigin.Begin);
            m_Stream.Write((ushort)written);
        }
    }

    public sealed class SetWarMode : Packet
    {
        public static readonly Packet InWarMode = Packet.SetStatic(new SetWarMode(true));
        public static readonly Packet InPeaceMode = Packet.SetStatic(new SetWarMode(false));

        public static Packet Instantiate(bool mode)
        {
            return (mode ? InWarMode : InPeaceMode);
        }

        public SetWarMode(bool mode) : base(0x72, 5)
        {
            m_Stream.Write(mode);
            m_Stream.Write((byte)0x00);
            m_Stream.Write((byte)0x32);
            m_Stream.Write((byte)0x00);
            //m_Stream.Fill();
        }
    }

    public sealed class Swing : Packet
    {
        public Swing(int flag, Mobile attacker, Mobile defender) : base(0x2F, 10)
        {
            m_Stream.Write((byte)flag);
            m_Stream.Write((int)attacker.Serial);
            m_Stream.Write((int)defender.Serial);
        }
    }

    public sealed class NullFastwalkStack : Packet
    {
        public NullFastwalkStack() : base(0xBF)
        {
            EnsureCapacity(256);
            m_Stream.Write((short)0x1);
            m_Stream.Write((int)0x0);
            m_Stream.Write((int)0x0);
            m_Stream.Write((int)0x0);
            m_Stream.Write((int)0x0);
            m_Stream.Write((int)0x0);
            m_Stream.Write((int)0x0);
        }
    }

    public sealed class RemoveItem : Packet
    {
        public RemoveItem(Item item) : base(0x1D, 5)
        {
            m_Stream.Write((int)item.Serial);
        }
    }

    public sealed class RemoveMobile : Packet
    {
        public RemoveMobile(Mobile m) : base(0x1D, 5)
        {
            m_Stream.Write((int)m.Serial);
        }
    }

    public sealed class ServerChange : Packet
    {
        public ServerChange(Mobile m, Map map) : base(0x76, 16)
        {
            m_Stream.Write((short)m.X);
            m_Stream.Write((short)m.Y);
            m_Stream.Write((short)m.Z);
            m_Stream.Write((byte)0);
            m_Stream.Write((short)0);
            m_Stream.Write((short)0);
            m_Stream.Write((short)map.Width);
            m_Stream.Write((short)map.Height);
        }
    }

    public sealed class SkillUpdate : Packet
    {
        public SkillUpdate(Skills skills) : base(0x3A)
        {
            this.EnsureCapacity(6 + (skills.Length * 9));

            m_Stream.Write((byte)0x02); // type: absolute, capped

            for (int i = 0; i < skills.Length; ++i)
            {
                Skill s = skills[i];

                double v = s.NonRacialValue;
                int uv = (int)(v * 10);

                if (uv < 0)
                    uv = 0;
                else if (uv >= 0x10000)
                    uv = 0xFFFF;

                m_Stream.Write((ushort)(s.Info.SkillID + 1));
                m_Stream.Write((ushort)uv);
                m_Stream.Write((ushort)s.BaseFixedPoint);
                m_Stream.Write((byte)s.Lock);
                m_Stream.Write((ushort)s.CapFixedPoint);
            }

            m_Stream.Write((short)0); // terminate
        }
    }

    public sealed class Sequence : Packet
    {
        public Sequence(int num) : base(0x7B, 2)
        {
            m_Stream.Write((byte)num);
        }
    }

    public sealed class SkillChange : Packet
    {
        public SkillChange(Skill skill) : base(0x3A)
        {
            this.EnsureCapacity(13);

            double v = skill.NonRacialValue;
            int uv = (int)(v * 10);

            if (uv < 0)
                uv = 0;
            else if (uv >= 0x10000)
                uv = 0xFFFF;

            m_Stream.Write((byte)0xDF); // type: delta, capped
            m_Stream.Write((ushort)skill.Info.SkillID);
            m_Stream.Write((ushort)uv);
            m_Stream.Write((ushort)skill.BaseFixedPoint);
            m_Stream.Write((byte)skill.Lock);
            m_Stream.Write((ushort)skill.CapFixedPoint);

            /*m_Stream.Write( (short) skill.Info.SkillID );
			m_Stream.Write( (short) (skill.Value * 10.0) );
			m_Stream.Write( (short) (skill.Base * 10.0) );
			m_Stream.Write( (byte) skill.Lock );
			m_Stream.Write( (short) skill.CapFixedPoint );*/
        }
    }

    public sealed class LaunchBrowser : Packet
    {
        public LaunchBrowser(string url) : base(0xA5)
        {
            if (url == null) url = "";

            this.EnsureCapacity(4 + url.Length);

            m_Stream.WriteAsciiNull(url);
        }
    }

    public sealed class MessageLocalized : Packet
    {
        private static MessageLocalized[] m_Cache_IntLoc = new MessageLocalized[15000];
        private static MessageLocalized[] m_Cache_CliLoc = new MessageLocalized[100000];
        private static MessageLocalized[] m_Cache_CliLocCmp = new MessageLocalized[5000];

        public static MessageLocalized InstantiateGeneric(int number)
        {
            MessageLocalized[] cache = null;
            int index = 0;

            if (number >= 3000000)
            {
                cache = m_Cache_IntLoc;
                index = number - 3000000;
            }
            else if (number >= 1000000)
            {
                cache = m_Cache_CliLoc;
                index = number - 1000000;
            }
            else if (number >= 500000)
            {
                cache = m_Cache_CliLocCmp;
                index = number - 500000;
            }

            MessageLocalized p;

            if (cache != null && index >= 0 && index < cache.Length)
            {
                p = cache[index];

                if (p == null)
                {
                    cache[index] = p = new MessageLocalized(Serial.MinusOne, -1, MessageType.Regular, 0x3B2, 3, number, "System", "");
                    p.SetStatic();
                }
            }
            else
            {
                p = new MessageLocalized(Serial.MinusOne, -1, MessageType.Regular, 0x3B2, 3, number, "System", "");
            }

            return p;
        }

        public MessageLocalized(Serial serial, int graphic, MessageType type, int hue, int font, int number, string name, string args) : base(0xC1)
        {
            if (name == null) name = "";
            if (args == null) args = "";

            if (hue == 0)
                hue = 0x3B2;

            this.EnsureCapacity(50 + (args.Length * 2));

            m_Stream.Write((int)serial);
            m_Stream.Write((short)graphic);
            m_Stream.Write((byte)type);
            m_Stream.Write((short)hue);
            m_Stream.Write((short)font);
            m_Stream.Write((int)number);
            m_Stream.WriteAsciiFixed(name, 30);
            m_Stream.WriteLittleUniNull(args);
        }
    }

    public sealed class MobileMoving : Packet
    {
        public MobileMoving(Mobile m, int noto) : base(0x77, 17)
        {
            Point3D loc = m.Location;

            int hue = m.Hue;

            if (m.SolidHueOverride >= 0)
                hue = m.SolidHueOverride;

            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.Body);
            m_Stream.Write((short)loc.m_X);
            m_Stream.Write((short)loc.m_Y);
            m_Stream.Write((sbyte)loc.m_Z);
            m_Stream.Write((byte)m.Direction);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)m.GetPacketFlags());
            m_Stream.Write((byte)noto);
        }
    }

    // Pre-7.0.0.0 Mobile Moving
    public sealed class MobileMovingOld : Packet
    {
        public MobileMovingOld(Mobile m, int noto) : base(0x77, 17)
        {
            Point3D loc = m.Location;

            int hue = m.Hue;

            if (m.SolidHueOverride >= 0)
                hue = m.SolidHueOverride;

            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.Body);
            m_Stream.Write((short)loc.m_X);
            m_Stream.Write((short)loc.m_Y);
            m_Stream.Write((sbyte)loc.m_Z);
            m_Stream.Write((byte)m.Direction);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)m.GetOldPacketFlags());
            m_Stream.Write((byte)noto);
        }
    }

    public sealed class MultiTargetReqHS : Packet
    {
        public MultiTargetReqHS(MultiTarget t) : base(0x99, 30)
        {
            m_Stream.Write((bool)t.AllowGround);
            m_Stream.Write((int)t.TargetID);
            m_Stream.Write((byte)t.Flags);

            m_Stream.Fill();

            m_Stream.Seek(18, SeekOrigin.Begin);
            m_Stream.Write((short)t.MultiID);
            m_Stream.Write((short)t.Offset.X);
            m_Stream.Write((short)t.Offset.Y);
            m_Stream.Write((short)t.Offset.Z);

            // DWORD Hue
        }
    }

    public sealed class MultiTargetReq : Packet
    {
        public MultiTargetReq(MultiTarget t) : base(0x99, 26)
        {
            m_Stream.Write((bool)t.AllowGround);
            m_Stream.Write((int)t.TargetID);
            m_Stream.Write((byte)t.Flags);

            m_Stream.Fill();

            m_Stream.Seek(18, SeekOrigin.Begin);
            m_Stream.Write((short)t.MultiID);
            m_Stream.Write((short)t.Offset.X);
            m_Stream.Write((short)t.Offset.Y);
            m_Stream.Write((short)t.Offset.Z);
        }
    }

    public sealed class CancelTarget : Packet
    {
        public static readonly Packet Instance = Packet.SetStatic(new CancelTarget());

        public CancelTarget() : base(0x6C, 19)
        {
            m_Stream.Write((byte)0);
            m_Stream.Write((int)0);
            m_Stream.Write((byte)3);
            m_Stream.Fill();
        }
    }

    public sealed class TargetReq : Packet
    {
        public TargetReq(Target t) : base(0x6C, 19)
        {
            m_Stream.Write((bool)t.AllowGround);
            m_Stream.Write((int)t.TargetID);
            m_Stream.Write((byte)t.Flags);
            m_Stream.Fill();
        }
    }

    public sealed class DragEffect : Packet
    {
        public DragEffect(IEntity src, IEntity trg, int itemID, int hue, int amount) : base(0x23, 26)
        {
            m_Stream.Write((short)itemID);
            m_Stream.Write((byte)0);
            m_Stream.Write((short)hue);
            m_Stream.Write((short)amount);
            m_Stream.Write((int)src.Serial);
            m_Stream.Write((short)src.X);
            m_Stream.Write((short)src.Y);
            m_Stream.Write((sbyte)src.Z);
            m_Stream.Write((int)trg.Serial);
            m_Stream.Write((short)trg.X);
            m_Stream.Write((short)trg.Y);
            m_Stream.Write((sbyte)trg.Z);
        }
    }

    public interface IGumpWriter
    {
        int TextEntries { get; set; }
        int Switches { get; set; }

        void AppendLayout(bool val);
        void AppendLayout(int val);
        void AppendLayoutNS(int val);
        void AppendLayout(string text);
        void AppendLayout(byte[] buffer);
        void WriteStrings(List<string> strings);
        void Flush();
    }

    public sealed class DisplayGumpPacked : Packet, IGumpWriter
    {
        private int m_TextEntries, m_Switches;

        public int TextEntries { get { return m_TextEntries; } set { m_TextEntries = value; } }
        public int Switches { get { return m_Switches; } set { m_Switches = value; } }

        private Gump m_Gump;

        private PacketWriter m_Layout;
        private PacketWriter m_Strings;

        private int m_StringCount;

        public DisplayGumpPacked(Gump gump)
            : base(0xDD)
        {
            m_Gump = gump;

            m_Layout = PacketWriter.CreateInstance(8192);
            m_Strings = PacketWriter.CreateInstance(8192);
        }

        private static byte[] m_True = Gump.StringToBuffer(" 1");
        private static byte[] m_False = Gump.StringToBuffer(" 0");

        private static byte[] m_BeginTextSeparator = Gump.StringToBuffer(" @");
        private static byte[] m_EndTextSeparator = Gump.StringToBuffer("@");

        private static byte[] m_Buffer = new byte[48];

        static DisplayGumpPacked()
        {
            m_Buffer[0] = (byte)' ';
        }

        public void AppendLayout(bool val)
        {
            AppendLayout(val ? m_True : m_False);
        }

        public void AppendLayout(int val)
        {
            string toString = val.ToString();
            int bytes = System.Text.Encoding.ASCII.GetBytes(toString, 0, toString.Length, m_Buffer, 1) + 1;

            m_Layout.Write(m_Buffer, 0, bytes);
        }

        public void AppendLayoutNS(int val)
        {
            string toString = val.ToString();
            int bytes = System.Text.Encoding.ASCII.GetBytes(toString, 0, toString.Length, m_Buffer, 1);

            m_Layout.Write(m_Buffer, 1, bytes);
        }

        public void AppendLayout(string text)
        {
            AppendLayout(m_BeginTextSeparator);

            m_Layout.WriteAsciiFixed(text, text.Length);

            AppendLayout(m_EndTextSeparator);
        }

        public void AppendLayout(byte[] buffer)
        {
            m_Layout.Write(buffer, 0, buffer.Length);
        }

        public void WriteStrings(List<string> strings)
        {
            m_StringCount = strings.Count;

            for (int i = 0; i < strings.Count; ++i)
            {
                string v = strings[i];

                if (v == null)
                    v = String.Empty;

                m_Strings.Write((ushort)v.Length);
                m_Strings.WriteBigUniFixed(v, v.Length);
            }
        }

        public void Flush()
        {
            EnsureCapacity(28 + (int)m_Layout.Length + (int)m_Strings.Length);

            m_Stream.Write((int)m_Gump.Serial);
            m_Stream.Write((int)m_Gump.TypeID);
            m_Stream.Write((int)m_Gump.X);
            m_Stream.Write((int)m_Gump.Y);

            // Note: layout MUST be null terminated (don't listen to krrios)
            m_Layout.Write((byte)0);
            WritePacked(m_Layout);

            m_Stream.Write((int)m_StringCount);

            WritePacked(m_Strings);

            PacketWriter.ReleaseInstance(m_Layout);
            PacketWriter.ReleaseInstance(m_Strings);
        }

        private const int GumpBufferSize = 0x5000;
        private static BufferPool m_PackBuffers = new BufferPool("Gump", 4, GumpBufferSize);

        private void WritePacked(PacketWriter src)
        {
            byte[] buffer = src.UnderlyingStream.GetBuffer();
            int length = (int)src.Length;

            if (length == 0)
            {
                m_Stream.Write((int)0);
                return;
            }

            int wantLength = 1 + ((buffer.Length * 1024) / 1000);

            wantLength += 4095;
            wantLength &= ~4095;

            byte[] m_PackBuffer;
            lock (m_PackBuffers)
                m_PackBuffer = m_PackBuffers.AcquireBuffer();

            if (m_PackBuffer.Length < wantLength)
            {
                Console.WriteLine("Notice: DisplayGumpPacked creating new {0} byte buffer", wantLength);
                m_PackBuffer = new byte[wantLength];
            }

            int packLength = m_PackBuffer.Length;

            Compression.Pack(m_PackBuffer, ref packLength, buffer, length, ZLibQuality.Default);

            m_Stream.Write((int)(4 + packLength));
            m_Stream.Write((int)length);
            m_Stream.Write(m_PackBuffer, 0, packLength);

            lock (m_PackBuffers)
                m_PackBuffers.ReleaseBuffer(m_PackBuffer);
        }
    }

    public sealed class DisplayGumpFast : Packet, IGumpWriter
    {
        private int m_TextEntries, m_Switches;

        private int m_LayoutLength;

        public int TextEntries { get { return m_TextEntries; } set { m_TextEntries = value; } }
        public int Switches { get { return m_Switches; } set { m_Switches = value; } }

        public DisplayGumpFast(Gump g) : base(0xB0)
        {
            m_Buffer[0] = (byte)' ';

            EnsureCapacity(4096);

            m_Stream.Write((int)g.Serial);
            m_Stream.Write((int)g.TypeID);
            m_Stream.Write((int)g.X);
            m_Stream.Write((int)g.Y);
            m_Stream.Write((ushort)0xFFFF);
        }

        private static byte[] m_True = Gump.StringToBuffer(" 1");
        private static byte[] m_False = Gump.StringToBuffer(" 0");

        private static byte[] m_BeginTextSeparator = Gump.StringToBuffer(" @");
        private static byte[] m_EndTextSeparator = Gump.StringToBuffer("@");

        private byte[] m_Buffer = new byte[48];

        public void AppendLayout(bool val)
        {
            AppendLayout(val ? m_True : m_False);
        }

        public void AppendLayout(int val)
        {
            string toString = val.ToString();
            int bytes = System.Text.Encoding.ASCII.GetBytes(toString, 0, toString.Length, m_Buffer, 1) + 1;

            m_Stream.Write(m_Buffer, 0, bytes);
            m_LayoutLength += bytes;
        }

        public void AppendLayoutNS(int val)
        {
            string toString = val.ToString();
            int bytes = System.Text.Encoding.ASCII.GetBytes(toString, 0, toString.Length, m_Buffer, 1);

            m_Stream.Write(m_Buffer, 1, bytes);
            m_LayoutLength += bytes;
        }

        public void AppendLayout(string text)
        {
            AppendLayout(m_BeginTextSeparator);

            int length = text.Length;
            m_Stream.WriteAsciiFixed(text, length);
            m_LayoutLength += length;

            AppendLayout(m_EndTextSeparator);
        }

        public void AppendLayout(byte[] buffer)
        {
            int length = buffer.Length;
            m_Stream.Write(buffer, 0, length);
            m_LayoutLength += length;
        }

        public void WriteStrings(List<string> text)
        {
            m_Stream.Seek(19, SeekOrigin.Begin);
            m_Stream.Write((ushort)m_LayoutLength);
            m_Stream.Seek(0, SeekOrigin.End);

            m_Stream.Write((ushort)text.Count);

            for (int i = 0; i < text.Count; ++i)
            {
                string v = text[i];

                if (v == null)
                    v = String.Empty;

                int length = (ushort)v.Length;

                m_Stream.Write((ushort)length);
                m_Stream.WriteBigUniFixed(v, length);
            }
        }

        public void Flush()
        {
        }
    }

    public sealed class DisplayGump : Packet
    {
        public DisplayGump(Gump g, string layout, string[] text) : base(0xB0)
        {
            if (layout == null) layout = "";

            this.EnsureCapacity(256);

            m_Stream.Write((int)g.Serial);
            m_Stream.Write((int)g.TypeID);
            m_Stream.Write((int)g.X);
            m_Stream.Write((int)g.Y);
            m_Stream.Write((ushort)(layout.Length + 1));
            m_Stream.WriteAsciiNull(layout);

            m_Stream.Write((ushort)text.Length);

            for (int i = 0; i < text.Length; ++i)
            {
                string v = text[i];

                if (v == null) v = "";

                int length = (ushort)v.Length;

                m_Stream.Write((ushort)length);
                m_Stream.WriteBigUniFixed(v, length);
            }
        }
    }

    public sealed class DisplayPaperdoll : Packet
    {
        public DisplayPaperdoll(Mobile m, string text, bool canLift) : base(0x88, 66)
        {
            byte flags = 0x00;

            if (m.Warmode)
                flags |= 0x01;

            if (canLift)
                flags |= 0x02;

            m_Stream.Write((int)m.Serial);
            m_Stream.WriteAsciiFixed(text, 60);
            m_Stream.Write((byte)flags);
        }
    }

    public sealed class PopupMessage : Packet
    {
        public PopupMessage(PMMessage msg) : base(0x53, 2)
        {
            m_Stream.Write((byte)msg);
        }
    }

    public sealed class PlaySound : Packet
    {
        public PlaySound(int soundID, IPoint3D target) : base(0x54, 12)
        {
            m_Stream.Write((byte)1); // flags
            m_Stream.Write((short)soundID);
            m_Stream.Write((short)0); // volume
            m_Stream.Write((short)target.X);
            m_Stream.Write((short)target.Y);
            m_Stream.Write((short)target.Z);
        }
    }

    public sealed class PlayMusic : Packet
    {
        public static readonly Packet InvalidInstance = Packet.SetStatic(new PlayMusic(MusicName.Invalid));

        private static Packet[] m_Instances = new Packet[60];

        public static Packet GetInstance(MusicName name)
        {
            if (name == MusicName.Invalid)
                return InvalidInstance;

            int v = (int)name;
            Packet p;

            if (v >= 0 && v < m_Instances.Length)
            {
                p = m_Instances[v];

                if (p == null)
                    m_Instances[v] = p = Packet.SetStatic(new PlayMusic(name));
            }
            else
            {
                p = new PlayMusic(name);
            }

            return p;
        }

        public PlayMusic(MusicName name) : base(0x6D, 3)
        {
            m_Stream.Write((short)name);
        }
    }

    public sealed class ScrollMessage : Packet
    {
        public ScrollMessage(int type, int tip, string text) : base(0xA6)
        {
            if (text == null) text = "";

            this.EnsureCapacity(10 + text.Length);

            m_Stream.Write((byte)type);
            m_Stream.Write((int)tip);
            m_Stream.Write((ushort)text.Length);
            m_Stream.WriteAsciiFixed(text, text.Length);
        }
    }

    public sealed class CurrentTime : Packet
    {
        public CurrentTime() : base(0x5B, 4)
        {
            DateTime now = DateTime.UtcNow;

            m_Stream.Write((byte)now.Hour);
            m_Stream.Write((byte)now.Minute);
            m_Stream.Write((byte)now.Second);
        }
    }

    public sealed class MapChange : Packet
    {
        public MapChange(Mobile m) : base(0xBF)
        {
            this.EnsureCapacity(6);

            m_Stream.Write((short)0x08);
            m_Stream.Write((byte)(m.Map == null ? 0 : m.Map.MapID));
        }
    }

    public sealed class SeasonChange : Packet
    {
        private static SeasonChange[][] m_Cache = new SeasonChange[5][]
            {
                new SeasonChange[2],
                new SeasonChange[2],
                new SeasonChange[2],
                new SeasonChange[2],
                new SeasonChange[2]
            };

        public static SeasonChange Instantiate(int season)
        {
            return Instantiate(season, true);
        }

        public static SeasonChange Instantiate(int season, bool playSound)
        {
            if (season >= 0 && season < m_Cache.Length)
            {
                int idx = playSound ? 1 : 0;

                SeasonChange p = m_Cache[season][idx];

                if (p == null)
                {
                    m_Cache[season][idx] = p = new SeasonChange(season, playSound);
                    p.SetStatic();
                }

                return p;
            }
            else
            {
                return new SeasonChange(season, playSound);
            }
        }

        public SeasonChange(int season) : this(season, true)
        {
        }

        public SeasonChange(int season, bool playSound) : base(0xBC, 3)
        {
            m_Stream.Write((byte)season);
            m_Stream.Write((bool)playSound);
        }
    }

    public sealed class SupportedFeatures : Packet
    {
        private static FeatureFlags m_AdditionalFlags;

        public static FeatureFlags Value { get { return m_AdditionalFlags; } set { m_AdditionalFlags = value; } }

        public static SupportedFeatures Instantiate(NetState ns)
        {
            return new SupportedFeatures(ns);
        }

        public SupportedFeatures(NetState ns) : base(0xB9, ns.ExtendedSupportedFeatures ? 5 : 3)
        {
            FeatureFlags flags = ExpansionInfo.CoreExpansion.SupportedFeatures;

            flags |= m_AdditionalFlags;

            IAccount acct = ns.Account as IAccount;

            if (acct != null && acct.Limit >= 6)
            {
                flags |= FeatureFlags.LiveAccount;
                flags &= ~FeatureFlags.UOTD;

                if (acct.Limit > 6)
                    flags |= FeatureFlags.SeventhCharacterSlot;
                else
                    flags |= FeatureFlags.SixthCharacterSlot;
            }

            if (ns.ExtendedSupportedFeatures)
            {
                m_Stream.Write((uint)flags);
            }
            else
            {
                m_Stream.Write((ushort)flags);
            }
        }
    }

    public static class AttributeNormalizer
    {
        private static int m_Maximum = 25;
        private static bool m_Enabled = true;

        public static int Maximum
        {
            get { return m_Maximum; }
            set { m_Maximum = value; }
        }

        public static bool Enabled
        {
            get { return m_Enabled; }
            set { m_Enabled = value; }
        }

        public static void Write(PacketWriter stream, int cur, int max)
        {
            if (m_Enabled && max != 0)
            {
                stream.Write((short)m_Maximum);
                stream.Write((short)((cur * m_Maximum) / max));
            }
            else
            {
                stream.Write((short)max);
                stream.Write((short)cur);
            }
        }

        public static void WriteReverse(PacketWriter stream, int cur, int max)
        {
            if (m_Enabled && max != 0)
            {
                stream.Write((short)((cur * m_Maximum) / max));
                stream.Write((short)m_Maximum);
            }
            else
            {
                stream.Write((short)cur);
                stream.Write((short)max);
            }
        }
    }

    public sealed class MobileHits : Packet
    {
        public MobileHits(Mobile m) : base(0xA1, 9)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.HitsMax);
            m_Stream.Write((short)m.Hits);
        }
    }

    public sealed class MobileHitsN : Packet
    {
        public MobileHitsN(Mobile m) : base(0xA1, 9)
        {
            m_Stream.Write((int)m.Serial);
            AttributeNormalizer.Write(m_Stream, m.Hits, m.HitsMax);
        }
    }

    public sealed class MobileMana : Packet
    {
        public MobileMana(Mobile m) : base(0xA2, 9)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.ManaMax);
            m_Stream.Write((short)m.Mana);
        }
    }

    public sealed class MobileManaN : Packet
    {
        public MobileManaN(Mobile m) : base(0xA2, 9)
        {
            m_Stream.Write((int)m.Serial);
            AttributeNormalizer.Write(m_Stream, m.Mana, m.ManaMax);
        }
    }

    public sealed class MobileStam : Packet
    {
        public MobileStam(Mobile m) : base(0xA3, 9)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.StamMax);
            m_Stream.Write((short)m.Stam);
        }
    }

    public sealed class MobileStamN : Packet
    {
        public MobileStamN(Mobile m) : base(0xA3, 9)
        {
            m_Stream.Write((int)m.Serial);
            AttributeNormalizer.Write(m_Stream, m.Stam, m.StamMax);
        }
    }

    public sealed class MobileAttributes : Packet
    {
        public MobileAttributes(Mobile m) : base(0x2D, 17)
        {
            m_Stream.Write(m.Serial);

            m_Stream.Write((short)m.HitsMax);
            m_Stream.Write((short)m.Hits);

            m_Stream.Write((short)m.ManaMax);
            m_Stream.Write((short)m.Mana);

            m_Stream.Write((short)m.StamMax);
            m_Stream.Write((short)m.Stam);
        }
    }

    public sealed class MobileAttributesN : Packet
    {
        public MobileAttributesN(Mobile m) : base(0x2D, 17)
        {
            m_Stream.Write(m.Serial);

            AttributeNormalizer.Write(m_Stream, m.Hits, m.HitsMax);
            AttributeNormalizer.Write(m_Stream, m.Mana, m.ManaMax);
            AttributeNormalizer.Write(m_Stream, m.Stam, m.StamMax);
        }
    }

    public sealed class PathfindMessage : Packet
    {
        public PathfindMessage(IPoint3D p) : base(0x38, 7)
        {
            m_Stream.Write((short)p.X);
            m_Stream.Write((short)p.Y);
            m_Stream.Write((short)p.Z);
        }
    }

    // unsure of proper format, client crashes
    public sealed class MobileName : Packet
    {
        public MobileName(Mobile m) : base(0x98)
        {
            string name = m.Name;

            if (name == null) name = "";

            this.EnsureCapacity(37);

            m_Stream.Write((int)m.Serial);
            m_Stream.WriteAsciiFixed(name, 30);
        }
    }

    public sealed class MobileAnimation : Packet
    {
        public MobileAnimation(Mobile m, int action, int frameCount, int repeatCount, bool forward, bool repeat, int delay) : base(0x6E, 14)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)action);
            m_Stream.Write((short)frameCount);
            m_Stream.Write((short)repeatCount);
            m_Stream.Write((bool)!forward); // protocol has really "reverse" but I find this more intuitive
            m_Stream.Write((bool)repeat);
            m_Stream.Write((byte)delay);
        }
    }

    public sealed class NewMobileAnimation : Packet
    {
        public NewMobileAnimation(Mobile m, int action, int frameCount, int delay) : base(0xE2, 10)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)action);
            m_Stream.Write((short)frameCount);
            m_Stream.Write((byte)delay);
        }
    }

    public sealed class MobileStatusCompact : Packet
    {
        public MobileStatusCompact(bool canBeRenamed, Mobile m) : base(0x11)
        {
            string name = m.Name;
            if (name == null) name = "";

            this.EnsureCapacity(43);

            m_Stream.Write((int)m.Serial);
            m_Stream.WriteAsciiFixed(name, 30);

            AttributeNormalizer.WriteReverse(m_Stream, m.Hits, m.HitsMax);

            m_Stream.Write(canBeRenamed);

            m_Stream.Write((byte)0); // type
        }
    }

    public sealed class MobileStatusExtended : Packet
    {
        public MobileStatusExtended(Mobile m) : this(m, m.NetState)
        {
        }

        public MobileStatusExtended(Mobile m, NetState ns) : base(0x11)
        {
            string name = m.Name;
            if (name == null) name = "";

            int type;

            if (Core.HS && ns != null && ns.ExtendedStatus)
            {
                type = 6;
                EnsureCapacity(121);
            }
            else if (Core.ML && ns != null && ns.SupportsExpansion(Expansion.ML))
            {
                type = 5;
                EnsureCapacity(91);
            }
            else
            {
                type = Core.AOS ? 4 : 3;
                EnsureCapacity(88);
            }

            m_Stream.Write((int)m.Serial);
            m_Stream.WriteAsciiFixed(name, 30);

            m_Stream.Write((short)m.Hits);
            m_Stream.Write((short)m.HitsMax);

            m_Stream.Write(m.CanBeRenamedBy(m));

            m_Stream.Write((byte)type);

            m_Stream.Write(m.Female);

            m_Stream.Write((short)m.Str);
            m_Stream.Write((short)m.Dex);
            m_Stream.Write((short)m.Int);

            m_Stream.Write((short)m.Stam);
            m_Stream.Write((short)m.StamMax);

            m_Stream.Write((short)m.Mana);
            m_Stream.Write((short)m.ManaMax);

            m_Stream.Write((int)m.TotalGold);
            m_Stream.Write((short)(Core.AOS ? m.PhysicalResistance : (int)(m.ArmorRating + 0.5)));
            m_Stream.Write((short)(Mobile.BodyWeight + m.TotalWeight));

            if (type >= 5)
            {
                m_Stream.Write((short)m.MaxWeight);
                m_Stream.Write((byte)(m.Race.RaceID + 1));  // Would be 0x00 if it's a non-ML enabled account but...
            }

            m_Stream.Write((short)m.StatCap);

            m_Stream.Write((byte)m.Followers);
            m_Stream.Write((byte)m.FollowersMax);

            if (type >= 4)
            {
                m_Stream.Write((short)m.FireResistance); // Fire
                m_Stream.Write((short)m.ColdResistance); // Cold
                m_Stream.Write((short)m.PoisonResistance); // Poison
                m_Stream.Write((short)m.EnergyResistance); // Energy
                m_Stream.Write((short)m.Luck); // Luck

                IWeapon weapon = m.Weapon;

                int min = 0, max = 0;

                if (weapon != null)
                    weapon.GetStatusDamage(m, out min, out max);

                m_Stream.Write((short)min); // Damage min
                m_Stream.Write((short)max); // Damage max

                m_Stream.Write((int)m.TithingPoints);
            }

            if (type >= 6)
            {
                for (int i = 0; i < 15; ++i)
                    m_Stream.Write((short)m.GetAOSStatus(i));
            }
        }
    }

    public sealed class MobileStatus : Packet
    {
        public MobileStatus(Mobile beholder, Mobile beheld) : this(beholder, beheld, beheld.NetState)
        {
        }

        public MobileStatus(Mobile beholder, Mobile beheld, NetState ns) : base(0x11)
        {
            string name = beheld.Name;
            if (name == null) name = "";

            int type;

            if (beholder != beheld)
            {
                type = 0;
                EnsureCapacity(43);
            }
            else if (Core.HS && ns != null && ns.ExtendedStatus)
            {
                type = 6;
                EnsureCapacity(121);
            }
            else if (Core.ML && ns != null && ns.SupportsExpansion(Expansion.ML))
            {
                type = 5;
                EnsureCapacity(91);
            }
            else
            {
                type = Core.AOS ? 4 : 3;
                EnsureCapacity(88);
            }

            m_Stream.Write(beheld.Serial);

            m_Stream.WriteAsciiFixed(name, 30);

            if (beholder == beheld)
                WriteAttr(beheld.Hits, beheld.HitsMax);
            else
                WriteAttrNorm(beheld.Hits, beheld.HitsMax);

            m_Stream.Write(beheld.CanBeRenamedBy(beholder));

            m_Stream.Write((byte)type);

            if (type > 0)
            {
                m_Stream.Write(beheld.Female);

                m_Stream.Write((short)beheld.Str);
                m_Stream.Write((short)beheld.Dex);
                m_Stream.Write((short)beheld.Int);

                WriteAttr(beheld.Stam, beheld.StamMax);
                WriteAttr(beheld.Mana, beheld.ManaMax);

                m_Stream.Write((int)beheld.TotalGold);
                m_Stream.Write((short)(Core.AOS ? beheld.PhysicalResistance : (int)(beheld.ArmorRating + 0.5)));
                m_Stream.Write((short)(Mobile.BodyWeight + beheld.TotalWeight));

                if (type >= 5)
                {
                    m_Stream.Write((short)beheld.MaxWeight);
                    m_Stream.Write((byte)(beheld.Race.RaceID + 1)); // Would be 0x00 if it's a non-ML enabled account but...
                }

                m_Stream.Write((short)beheld.StatCap);

                m_Stream.Write((byte)beheld.Followers);
                m_Stream.Write((byte)beheld.FollowersMax);

                if (type >= 4)
                {
                    m_Stream.Write((short)beheld.FireResistance); // Fire
                    m_Stream.Write((short)beheld.ColdResistance); // Cold
                    m_Stream.Write((short)beheld.PoisonResistance); // Poison
                    m_Stream.Write((short)beheld.EnergyResistance); // Energy
                    m_Stream.Write((short)beheld.Luck); // Luck

                    IWeapon weapon = beheld.Weapon;

                    int min = 0, max = 0;

                    if (weapon != null)
                        weapon.GetStatusDamage(beheld, out min, out max);

                    m_Stream.Write((short)min); // Damage min
                    m_Stream.Write((short)max); // Damage max

                    m_Stream.Write((int)beheld.TithingPoints);
                }

                if (type >= 6)
                {
                    for (int i = 0; i < 15; ++i)
                        m_Stream.Write((short)beheld.GetAOSStatus(i));
                }
            }
        }

        private void WriteAttr(int current, int maximum)
        {
            m_Stream.Write((short)current);
            m_Stream.Write((short)maximum);
        }

        private void WriteAttrNorm(int current, int maximum)
        {
            AttributeNormalizer.WriteReverse(m_Stream, current, maximum);
        }
    }

    public sealed class HealthbarPoison : Packet
    {
        public HealthbarPoison(Mobile m) : base(0x17)
        {
            EnsureCapacity(12);

            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)1);

            m_Stream.Write((short)1);

            Poison p = m.Poison;

            if (p != null)
            {
                m_Stream.Write((byte)(p.Level + 1));
            }
            else
            {
                m_Stream.Write((byte)0);
            }
        }
    }

    public sealed class HealthbarYellow : Packet
    {
        public HealthbarYellow(Mobile m) : base(0x17)
        {
            EnsureCapacity(12);

            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)1);

            m_Stream.Write((short)2);

            if (m.Blessed || m.YellowHealthbar)
            {
                m_Stream.Write((byte)1);
            }
            else
            {
                m_Stream.Write((byte)0);
            }
        }
    }

    public sealed class MobileUpdate : Packet
    {
        public MobileUpdate(Mobile m) : base(0x20, 19)
        {
            int hue = m.Hue;

            if (m.SolidHueOverride >= 0)
                hue = m.SolidHueOverride;

            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.Body);
            m_Stream.Write((byte)0);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)m.GetPacketFlags());
            m_Stream.Write((short)m.X);
            m_Stream.Write((short)m.Y);
            m_Stream.Write((short)0);
            m_Stream.Write((byte)m.Direction);
            m_Stream.Write((sbyte)m.Z);
        }
    }

    // Pre-7.0.0.0 Mobile Update
    public sealed class MobileUpdateOld : Packet
    {
        public MobileUpdateOld(Mobile m) : base(0x20, 19)
        {
            int hue = m.Hue;

            if (m.SolidHueOverride >= 0)
                hue = m.SolidHueOverride;

            m_Stream.Write((int)m.Serial);
            m_Stream.Write((short)m.Body);
            m_Stream.Write((byte)0);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)m.GetOldPacketFlags());
            m_Stream.Write((short)m.X);
            m_Stream.Write((short)m.Y);
            m_Stream.Write((short)0);
            m_Stream.Write((byte)m.Direction);
            m_Stream.Write((sbyte)m.Z);
        }
    }

    public sealed class MobileIncoming : Packet
    {
        public static Packet Create(NetState ns, Mobile beholder, Mobile beheld)
        {
            if (ns.NewMobileIncoming)
                return new MobileIncoming(beholder, beheld);
            else if (ns.StygianAbyss)
                return new MobileIncomingSA(beholder, beheld);
            else
                return new MobileIncomingOld(beholder, beheld);
        }

        private static ThreadLocal<int[]> m_DupedLayersTL = new ThreadLocal<int[]>(() => { return new int[256]; });
        private static ThreadLocal<int> m_VersionTL = new ThreadLocal<int>();

        public Mobile m_Beheld;

        public MobileIncoming(Mobile beholder, Mobile beheld) : base(0x78)
        {
            m_Beheld = beheld;

            int m_Version = ++(m_VersionTL.Value);
            int[] m_DupedLayers = m_DupedLayersTL.Value;

            List<Item> eq = beheld.Items;
            int count = eq.Count;

            if (beheld.HairItemID > 0)
                count++;
            if (beheld.FacialHairItemID > 0)
                count++;

            this.EnsureCapacity(23 + (count * 9));

            int hue = beheld.Hue;

            if (beheld.SolidHueOverride >= 0)
                hue = beheld.SolidHueOverride;

            m_Stream.Write((int)beheld.Serial);
            m_Stream.Write((short)beheld.Body);
            m_Stream.Write((short)beheld.X);
            m_Stream.Write((short)beheld.Y);
            m_Stream.Write((sbyte)beheld.Z);
            m_Stream.Write((byte)beheld.Direction);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)beheld.GetPacketFlags());
            m_Stream.Write((byte)Notoriety.Compute(beholder, beheld));

            for (int i = 0; i < eq.Count; ++i)
            {
                Item item = eq[i];

                byte layer = (byte)item.Layer;

                if (!item.Deleted && beholder.CanSee(item) && m_DupedLayers[layer] != m_Version)
                {
                    m_DupedLayers[layer] = m_Version;

                    hue = item.Hue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = item.ItemID & 0xFFFF;

                    m_Stream.Write((int)item.Serial);
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)layer);

                    m_Stream.Write((short)hue);
                }
            }

            if (beheld.HairItemID > 0)
            {
                if (m_DupedLayers[(int)Layer.Hair] != m_Version)
                {
                    m_DupedLayers[(int)Layer.Hair] = m_Version;
                    hue = beheld.HairHue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = beheld.HairItemID & 0xFFFF;

                    m_Stream.Write((int)HairInfo.FakeSerial(beheld));
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)Layer.Hair);

                    m_Stream.Write((short)hue);
                }
            }

            if (beheld.FacialHairItemID > 0)
            {
                if (m_DupedLayers[(int)Layer.FacialHair] != m_Version)
                {
                    m_DupedLayers[(int)Layer.FacialHair] = m_Version;
                    hue = beheld.FacialHairHue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = beheld.FacialHairItemID & 0xFFFF;

                    m_Stream.Write((int)FacialHairInfo.FakeSerial(beheld));
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)Layer.FacialHair);

                    m_Stream.Write((short)hue);
                }
            }

            m_Stream.Write((int)0); // terminate
        }
    }

    public sealed class MobileIncomingSA : Packet
    {
        private static ThreadLocal<int[]> m_DupedLayersTL = new ThreadLocal<int[]>(() => { return new int[256]; });
        private static ThreadLocal<int> m_VersionTL = new ThreadLocal<int>();

        public Mobile m_Beheld;

        public MobileIncomingSA(Mobile beholder, Mobile beheld) : base(0x78)
        {
            m_Beheld = beheld;

            int m_Version = ++(m_VersionTL.Value);
            int[] m_DupedLayers = m_DupedLayersTL.Value;

            List<Item> eq = beheld.Items;
            int count = eq.Count;

            if (beheld.HairItemID > 0)
                count++;
            if (beheld.FacialHairItemID > 0)
                count++;

            this.EnsureCapacity(23 + (count * 9));

            int hue = beheld.Hue;

            if (beheld.SolidHueOverride >= 0)
                hue = beheld.SolidHueOverride;

            m_Stream.Write((int)beheld.Serial);
            m_Stream.Write((short)beheld.Body);
            m_Stream.Write((short)beheld.X);
            m_Stream.Write((short)beheld.Y);
            m_Stream.Write((sbyte)beheld.Z);
            m_Stream.Write((byte)beheld.Direction);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)beheld.GetPacketFlags());
            m_Stream.Write((byte)Notoriety.Compute(beholder, beheld));

            for (int i = 0; i < eq.Count; ++i)
            {
                Item item = eq[i];

                byte layer = (byte)item.Layer;

                if (!item.Deleted && beholder.CanSee(item) && m_DupedLayers[layer] != m_Version)
                {
                    m_DupedLayers[layer] = m_Version;

                    hue = item.Hue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = item.ItemID & 0x7FFF;
                    bool writeHue = (hue != 0);

                    if (writeHue)
                        itemID |= 0x8000;

                    m_Stream.Write((int)item.Serial);
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)layer);

                    if (writeHue)
                        m_Stream.Write((short)hue);
                }
            }

            if (beheld.HairItemID > 0)
            {
                if (m_DupedLayers[(int)Layer.Hair] != m_Version)
                {
                    m_DupedLayers[(int)Layer.Hair] = m_Version;
                    hue = beheld.HairHue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = beheld.HairItemID & 0x7FFF;

                    bool writeHue = (hue != 0);

                    if (writeHue)
                        itemID |= 0x8000;

                    m_Stream.Write((int)HairInfo.FakeSerial(beheld));
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)Layer.Hair);

                    if (writeHue)
                        m_Stream.Write((short)hue);
                }
            }

            if (beheld.FacialHairItemID > 0)
            {
                if (m_DupedLayers[(int)Layer.FacialHair] != m_Version)
                {
                    m_DupedLayers[(int)Layer.FacialHair] = m_Version;
                    hue = beheld.FacialHairHue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = beheld.FacialHairItemID & 0x7FFF;

                    bool writeHue = (hue != 0);

                    if (writeHue)
                        itemID |= 0x8000;

                    m_Stream.Write((int)FacialHairInfo.FakeSerial(beheld));
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)Layer.FacialHair);

                    if (writeHue)
                        m_Stream.Write((short)hue);
                }
            }

            m_Stream.Write((int)0); // terminate
        }
    }

    // Pre-7.0.0.0 Mobile Incoming
    public sealed class MobileIncomingOld : Packet
    {
        private static ThreadLocal<int[]> m_DupedLayersTL = new ThreadLocal<int[]>(() => { return new int[256]; });
        private static ThreadLocal<int> m_VersionTL = new ThreadLocal<int>();

        public Mobile m_Beheld;

        public MobileIncomingOld(Mobile beholder, Mobile beheld) : base(0x78)
        {
            m_Beheld = beheld;

            int m_Version = ++(m_VersionTL.Value);
            int[] m_DupedLayers = m_DupedLayersTL.Value;

            List<Item> eq = beheld.Items;
            int count = eq.Count;

            if (beheld.HairItemID > 0)
                count++;
            if (beheld.FacialHairItemID > 0)
                count++;

            this.EnsureCapacity(23 + (count * 9));

            int hue = beheld.Hue;

            if (beheld.SolidHueOverride >= 0)
                hue = beheld.SolidHueOverride;

            m_Stream.Write((int)beheld.Serial);
            m_Stream.Write((short)beheld.Body);
            m_Stream.Write((short)beheld.X);
            m_Stream.Write((short)beheld.Y);
            m_Stream.Write((sbyte)beheld.Z);
            m_Stream.Write((byte)beheld.Direction);
            m_Stream.Write((short)hue);
            m_Stream.Write((byte)beheld.GetOldPacketFlags());
            m_Stream.Write((byte)Notoriety.Compute(beholder, beheld));

            for (int i = 0; i < eq.Count; ++i)
            {
                Item item = eq[i];

                byte layer = (byte)item.Layer;

                if (!item.Deleted && beholder.CanSee(item) && m_DupedLayers[layer] != m_Version)
                {
                    m_DupedLayers[layer] = m_Version;

                    hue = item.Hue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = item.ItemID & 0x7FFF;
                    bool writeHue = (hue != 0);

                    if (writeHue)
                        itemID |= 0x8000;

                    m_Stream.Write((int)item.Serial);
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)layer);

                    if (writeHue)
                        m_Stream.Write((short)hue);
                }
            }

            if (beheld.HairItemID > 0)
            {
                if (m_DupedLayers[(int)Layer.Hair] != m_Version)
                {
                    m_DupedLayers[(int)Layer.Hair] = m_Version;
                    hue = beheld.HairHue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = beheld.HairItemID & 0x7FFF;

                    bool writeHue = (hue != 0);

                    if (writeHue)
                        itemID |= 0x8000;

                    m_Stream.Write((int)HairInfo.FakeSerial(beheld));
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)Layer.Hair);

                    if (writeHue)
                        m_Stream.Write((short)hue);
                }
            }

            if (beheld.FacialHairItemID > 0)
            {
                if (m_DupedLayers[(int)Layer.FacialHair] != m_Version)
                {
                    m_DupedLayers[(int)Layer.FacialHair] = m_Version;
                    hue = beheld.FacialHairHue;

                    if (beheld.SolidHueOverride >= 0)
                        hue = beheld.SolidHueOverride;

                    int itemID = beheld.FacialHairItemID & 0x7FFF;

                    bool writeHue = (hue != 0);

                    if (writeHue)
                        itemID |= 0x8000;

                    m_Stream.Write((int)FacialHairInfo.FakeSerial(beheld));
                    m_Stream.Write((ushort)itemID);
                    m_Stream.Write((byte)Layer.FacialHair);

                    if (writeHue)
                        m_Stream.Write((short)hue);
                }
            }

            m_Stream.Write((int)0); // terminate
        }
    }

    public sealed class AsciiMessage : Packet
    {
        public AsciiMessage(Serial serial, int graphic, MessageType type, int hue, int font, string name, string text) : base(0x1C)
        {
            if (name == null)
                name = "";

            if (text == null)
                text = "";

            if (hue == 0)
                hue = 0x3B2;

            this.EnsureCapacity(45 + text.Length);

            m_Stream.Write((int)serial);
            m_Stream.Write((short)graphic);
            m_Stream.Write((byte)type);
            m_Stream.Write((short)hue);
            m_Stream.Write((short)font);
            m_Stream.WriteAsciiFixed(name, 30);
            m_Stream.WriteAsciiNull(text);
        }
    }

    public sealed class UnicodeMessage : Packet
    {
        public UnicodeMessage(Serial serial, int graphic, MessageType type, int hue, int font, string lang, string name, string text) : base(0xAE)
        {
            if (string.IsNullOrEmpty(lang)) lang = "ENU";
            if (name == null) name = "";
            if (text == null) text = "";

            if (hue == 0)
                hue = 0x3B2;

            this.EnsureCapacity(50 + (text.Length * 2));

            m_Stream.Write((int)serial);
            m_Stream.Write((short)graphic);
            m_Stream.Write((byte)type);
            m_Stream.Write((short)hue);
            m_Stream.Write((short)font);
            m_Stream.WriteAsciiFixed(lang, 4);
            m_Stream.WriteAsciiFixed(name, 30);
            m_Stream.WriteBigUniNull(text);
        }
    }

    public sealed class PingAck : Packet
    {
        private static PingAck[] m_Cache = new PingAck[0x100];

        public static PingAck Instantiate(byte ping)
        {
            PingAck p = m_Cache[ping];

            if (p == null)
            {
                m_Cache[ping] = p = new PingAck(ping);
                p.SetStatic();
            }

            return p;
        }

        public PingAck(byte ping) : base(0x73, 2)
        {
            m_Stream.Write(ping);
        }
    }

    public sealed class MovementRej : Packet
    {
        public MovementRej(int seq, Mobile m) : base(0x21, 8)
        {
            m_Stream.Write((byte)seq);
            m_Stream.Write((short)m.X);
            m_Stream.Write((short)m.Y);
            m_Stream.Write((byte)m.Direction);
            m_Stream.Write((sbyte)m.Z);
        }
    }

    public sealed class MovementAck : Packet
    {
        private static MovementAck[][] m_Cache = new MovementAck[8][]
            {
                new MovementAck[256],
                new MovementAck[256],
                new MovementAck[256],
                new MovementAck[256],
                new MovementAck[256],
                new MovementAck[256],
                new MovementAck[256],
                new MovementAck[256]
            };

        public static MovementAck Instantiate(int seq, Mobile m)
        {
            int noto = Notoriety.Compute(m, m);

            MovementAck p = m_Cache[noto][seq];

            if (p == null)
            {
                m_Cache[noto][seq] = p = new MovementAck(seq, noto);
                p.SetStatic();
            }

            return p;
        }

        private MovementAck(int seq, int noto) : base(0x22, 3)
        {
            m_Stream.Write((byte)seq);
            m_Stream.Write((byte)noto);
        }
    }

    public sealed class LoginConfirm : Packet
    {
        public LoginConfirm(Mobile m) : base(0x1B, 37)
        {
            m_Stream.Write((int)m.Serial);
            m_Stream.Write((int)0);
            m_Stream.Write((short)m.Body);
            m_Stream.Write((short)m.X);
            m_Stream.Write((short)m.Y);
            m_Stream.Write((short)m.Z);
            m_Stream.Write((byte)m.Direction);
            m_Stream.Write((byte)0);
            m_Stream.Write((int)-1);

            Map map = m.Map;

            if (map == null || map == Map.Internal)
                map = m.LogoutMap;

            m_Stream.Write((short)0);
            m_Stream.Write((short)0);
            m_Stream.Write((short)(map == null ? 6144 : map.Width));
            m_Stream.Write((short)(map == null ? 4096 : map.Height));

            m_Stream.Fill();
        }
    }

    public sealed class LoginComplete : Packet
    {
        public static readonly Packet Instance = Packet.SetStatic(new LoginComplete());

        public LoginComplete() : base(0x55, 1)
        {
        }
    }

    public sealed class CityInfo
    {
        private string m_City;
        private string m_Building;
        private int m_Description;
        private Point3D m_Location;
        private Map m_Map;

        public CityInfo(string city, string building, int description, int x, int y, int z, Map m)
        {
            m_City = city;
            m_Building = building;
            m_Description = description;
            m_Location = new Point3D(x, y, z);
            m_Map = m;
        }

        public CityInfo(string city, string building, int x, int y, int z, Map m) : this(city, building, 0, x, y, z, m)
        {
        }

        public CityInfo(string city, string building, int description, int x, int y, int z) : this(city, building, description, x, y, z, Map.Trammel)
        {
        }

        public CityInfo(string city, string building, int x, int y, int z) : this(city, building, 0, x, y, z, Map.Trammel)
        {
        }

        public string City
        {
            get
            {
                return m_City;
            }
            set
            {
                m_City = value;
            }
        }

        public string Building
        {
            get
            {
                return m_Building;
            }
            set
            {
                m_Building = value;
            }
        }

        public int Description
        {
            get
            {
                return m_Description;
            }
            set
            {
                m_Description = value;
            }
        }

        public int X
        {
            get
            {
                return m_Location.X;
            }
            set
            {
                m_Location.X = value;
            }
        }

        public int Y
        {
            get
            {
                return m_Location.Y;
            }
            set
            {
                m_Location.Y = value;
            }
        }

        public int Z
        {
            get
            {
                return m_Location.Z;
            }
            set
            {
                m_Location.Z = value;
            }
        }

        public Point3D Location
        {
            get
            {
                return m_Location;
            }
            set
            {
                m_Location = value;
            }
        }

        public Map Map
        {
            get { return m_Map; }
            set { m_Map = value; }
        }
    }

    public sealed class CharacterListUpdate : Packet
    {
        public CharacterListUpdate(IAccount a) : base(0x86)
        {
            this.EnsureCapacity(4 + (a.Length * 60));

            int highSlot = -1;

            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != null)
                    highSlot = i;
            }

            int count = Math.Max(Math.Max(highSlot + 1, a.Limit), 5);

            m_Stream.Write((byte)count);

            for (int i = 0; i < count; ++i)
            {
                Mobile m = a[i];

                if (m != null)
                {
                    m_Stream.WriteAsciiFixed(m.Name, 30);
                    m_Stream.Fill(30); // password
                }
                else
                {
                    m_Stream.Fill(60);
                }
            }
        }
    }

    [Flags]
    public enum ThirdPartyFeature : ulong
    {
        FilterWeather = 1 << 0,
        FilterLight = 1 << 1,

        SmartTarget = 1 << 2,
        RangedTarget = 1 << 3,

        AutoOpenDoors = 1 << 4,

        DequipOnCast = 1 << 5,
        AutoPotionEquip = 1 << 6,

        ProtectHeals = 1 << 7,

        LoopedMacros = 1 << 8,

        UseOnceAgent = 1 << 9,
        RestockAgent = 1 << 10,
        SellAgent = 1 << 11,
        BuyAgent = 1 << 12,

        PotionHotkeys = 1 << 13,

        RandomTargets = 1 << 14,
        ClosestTargets = 1 << 15, // All closest target hotkeys
        OverheadHealth = 1 << 16, // Health and Mana/Stam messages shown over player's heads

        AutolootAgent = 1 << 17,
        BoneCutterAgent = 1 << 18,
        AdvancedMacros = 1 << 19,
        AutoRemount = 1 << 20,
        AutoBandage = 1 << 21,
        EnemyTargetShare = 1 << 22,
        FilterSeason = 1 << 23,
        SpellTargetShare = 1 << 24,

        All = ulong.MaxValue
    }

    public static class FeatureProtection
    {
        private static ThirdPartyFeature m_Disabled = 0;

        public static ThirdPartyFeature DisabledFeatures
        {
            get { return m_Disabled; }
        }

        public static void Disable(ThirdPartyFeature feature)
        {
            SetDisabled(feature, true);
        }

        public static void Enable(ThirdPartyFeature feature)
        {
            SetDisabled(feature, false);
        }

        public static void SetDisabled(ThirdPartyFeature feature, bool value)
        {
            if (value)
                m_Disabled |= feature;
            else
                m_Disabled &= ~feature;
        }
    }

    public sealed class CharacterList : Packet
    {
        public CharacterList(IAccount a, CityInfo[] info) : base(0xA9)
        {
            this.EnsureCapacity(11 + (a.Length * 60) + (info.Length * 89));

            int highSlot = -1;

            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != null)
                    highSlot = i;
            }

            int count = Math.Max(Math.Max(highSlot + 1, a.Limit), 5);

            m_Stream.Write((byte)count);

            for (int i = 0; i < count; ++i)
            {
                if (a[i] != null)
                {
                    m_Stream.WriteAsciiFixed(a[i].Name, 30);
                    m_Stream.Fill(30); // password
                }
                else
                {
                    m_Stream.Fill(60);
                }
            }

            m_Stream.Write((byte)info.Length);

            for (int i = 0; i < info.Length; ++i)
            {
                CityInfo ci = info[i];

                m_Stream.Write((byte)i);
                m_Stream.WriteAsciiFixed(ci.City, 32);
                m_Stream.WriteAsciiFixed(ci.Building, 32);
                m_Stream.Write((int)ci.X);
                m_Stream.Write((int)ci.Y);
                m_Stream.Write((int)ci.Z);
                m_Stream.Write((int)ci.Map.MapID);
                m_Stream.Write((int)ci.Description);
                m_Stream.Write((int)0);
            }

            CharacterListFlags flags = ExpansionInfo.CoreExpansion.CharacterListFlags;

            if (count > 6)
                flags |= (CharacterListFlags.SeventhCharacterSlot | CharacterListFlags.SixthCharacterSlot); // 7th Character Slot - TODO: Is SixthCharacterSlot Required?
            else if (count == 6)
                flags |= CharacterListFlags.SixthCharacterSlot; // 6th Character Slot
            else if (a.Limit == 1)
                flags |= (CharacterListFlags.SlotLimit & CharacterListFlags.OneCharacterSlot); // Limit Characters & One Character

            m_Stream.Write((int)(flags | m_AdditionalFlags)); // Additional Flags

            m_Stream.Write((short)-1);

            ThirdPartyFeature disabled = FeatureProtection.DisabledFeatures;

            if (disabled != 0)
            {
                if (m_MD5Provider == null)
                    m_MD5Provider = new System.Security.Cryptography.MD5CryptoServiceProvider();

                m_Stream.UnderlyingStream.Flush();

                byte[] hashCode = m_MD5Provider.ComputeHash(m_Stream.UnderlyingStream.GetBuffer(), 0, (int)m_Stream.UnderlyingStream.Length);
                byte[] buffer = new byte[28];

                for (int i = 0; i < count; ++i)
                {
                    Utility.RandomBytes(buffer);

                    m_Stream.Seek(35 + (i * 60), SeekOrigin.Begin);
                    m_Stream.Write(buffer, 0, buffer.Length);
                }

                m_Stream.Seek(35, SeekOrigin.Begin);
                m_Stream.Write((int)((long)disabled >> 32));
                m_Stream.Write((int)disabled);

                m_Stream.Seek(95, SeekOrigin.Begin);
                m_Stream.Write(hashCode, 0, hashCode.Length);
            }
        }

        private static System.Security.Cryptography.MD5CryptoServiceProvider m_MD5Provider;

        private static CharacterListFlags m_AdditionalFlags;

        public static CharacterListFlags AdditionalFlags
        {
            get { return m_AdditionalFlags; }
            set { m_AdditionalFlags = value; }
        }
    }

    public sealed class CharacterListOld : Packet
    {
        public CharacterListOld(IAccount a, CityInfo[] info) : base(0xA9)
        {
            this.EnsureCapacity(9 + (a.Length * 60) + (info.Length * 63));

            int highSlot = -1;

            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != null)
                    highSlot = i;
            }

            int count = Math.Max(Math.Max(highSlot + 1, a.Limit), 5);

            m_Stream.Write((byte)count);

            for (int i = 0; i < count; ++i)
            {
                if (a[i] != null)
                {
                    m_Stream.WriteAsciiFixed(a[i].Name, 30);
                    m_Stream.Fill(30); // password
                }
                else
                {
                    m_Stream.Fill(60);
                }
            }

            m_Stream.Write((byte)info.Length);

            for (int i = 0; i < info.Length; ++i)
            {
                CityInfo ci = info[i];

                m_Stream.Write((byte)i);
                m_Stream.WriteAsciiFixed(ci.City, 31);
                m_Stream.WriteAsciiFixed(ci.Building, 31);
            }

            CharacterListFlags flags = ExpansionInfo.CoreExpansion.CharacterListFlags;

            if (count > 6)
                flags |= (CharacterListFlags.SeventhCharacterSlot | CharacterListFlags.SixthCharacterSlot); // 7th Character Slot - TODO: Is SixthCharacterSlot Required?
            else if (count == 6)
                flags |= CharacterListFlags.SixthCharacterSlot; // 6th Character Slot
            else if (a.Limit == 1)
                flags |= (CharacterListFlags.SlotLimit & CharacterListFlags.OneCharacterSlot); // Limit Characters & One Character

            m_Stream.Write((int)(flags | CharacterList.AdditionalFlags)); // Additional Flags

            ThirdPartyFeature disabled = FeatureProtection.DisabledFeatures;

            if (disabled != 0)
            {
                if (m_MD5Provider == null)
                    m_MD5Provider = new System.Security.Cryptography.MD5CryptoServiceProvider();

                m_Stream.UnderlyingStream.Flush();

                byte[] hashCode = m_MD5Provider.ComputeHash(m_Stream.UnderlyingStream.GetBuffer(), 0, (int)m_Stream.UnderlyingStream.Length);
                byte[] buffer = new byte[28];

                for (int i = 0; i < count; ++i)
                {
                    Utility.RandomBytes(buffer);

                    m_Stream.Seek(35 + (i * 60), SeekOrigin.Begin);
                    m_Stream.Write(buffer, 0, buffer.Length);
                }

                m_Stream.Seek(35, SeekOrigin.Begin);
                m_Stream.Write((int)((long)disabled >> 32));
                m_Stream.Write((int)disabled);

                m_Stream.Seek(95, SeekOrigin.Begin);
                m_Stream.Write(hashCode, 0, hashCode.Length);
            }
        }

        private static System.Security.Cryptography.MD5CryptoServiceProvider m_MD5Provider;
    }

    public sealed class ClearWeaponAbility : Packet
    {
        public static readonly Packet Instance = Packet.SetStatic(new ClearWeaponAbility());

        public ClearWeaponAbility() : base(0xBF)
        {
            EnsureCapacity(5);

            m_Stream.Write((short)0x21);
        }
    }

    public enum ALRReason : byte
    {
        Invalid = 0x00,
        InUse = 0x01,
        Blocked = 0x02,
        BadPass = 0x03,
        Idle = 0xFE,
        BadComm = 0xFF
    }

    public sealed class AccountLoginRej : Packet
    {
        public AccountLoginRej(ALRReason reason) : base(0x82, 2)
        {
            m_Stream.Write((byte)reason);
        }
    }

    public enum AffixType : byte
    {
        Append = 0x00,
        Prepend = 0x01,
        System = 0x02
    }

    public sealed class MessageLocalizedAffix : Packet
    {
        public MessageLocalizedAffix(Serial serial, int graphic, MessageType messageType, int hue, int font, int number, string name, AffixType affixType, string affix, string args) : base(0xCC)
        {
            if (name == null) name = "";
            if (affix == null) affix = "";
            if (args == null) args = "";

            if (hue == 0)
                hue = 0x3B2;

            this.EnsureCapacity(52 + affix.Length + (args.Length * 2));

            m_Stream.Write((int)serial);
            m_Stream.Write((short)graphic);
            m_Stream.Write((byte)messageType);
            m_Stream.Write((short)hue);
            m_Stream.Write((short)font);
            m_Stream.Write((int)number);
            m_Stream.Write((byte)affixType);
            m_Stream.WriteAsciiFixed(name, 30);
            m_Stream.WriteAsciiNull(affix);
            m_Stream.WriteBigUniNull(args);
        }
    }

    public sealed class ServerInfo
    {
        private string m_Name;
        private int m_FullPercent;
        private int m_TimeZone;
        private IPEndPoint m_Address;

        public string Name
        {
            get
            {
                return m_Name;
            }
            set
            {
                m_Name = value;
            }
        }

        public int FullPercent
        {
            get
            {
                return m_FullPercent;
            }
            set
            {
                m_FullPercent = value;
            }
        }

        public int TimeZone
        {
            get
            {
                return m_TimeZone;
            }
            set
            {
                m_TimeZone = value;
            }
        }

        public IPEndPoint Address
        {
            get
            {
                return m_Address;
            }
            set
            {
                m_Address = value;
            }
        }

        public ServerInfo(string name, int fullPercent, TimeZone tz, IPEndPoint address)
        {
            m_Name = name;
            m_FullPercent = fullPercent;
            m_TimeZone = tz.GetUtcOffset(DateTime.Now).Hours;
            m_Address = address;
        }
    }

    public sealed class FollowMessage : Packet
    {
        public FollowMessage(Serial serial1, Serial serial2) : base(0x15, 9)
        {
            m_Stream.Write((int)serial1);
            m_Stream.Write((int)serial2);
        }
    }

    public sealed class AccountLoginAck : Packet
    {
        public AccountLoginAck(ServerInfo[] info) : base(0xA8)
        {
            this.EnsureCapacity(6 + (info.Length * 40));

            m_Stream.Write((byte)0x5D); // Unknown

            m_Stream.Write((ushort)info.Length);

            for (int i = 0; i < info.Length; ++i)
            {
                ServerInfo si = info[i];

                m_Stream.Write((ushort)i);
                m_Stream.WriteAsciiFixed(si.Name, 32);
                m_Stream.Write((byte)si.FullPercent);
                m_Stream.Write((sbyte)si.TimeZone);
                m_Stream.Write((int)Utility.GetAddressValue(si.Address.Address));
            }
        }
    }

    public sealed class DisplaySignGump : Packet
    {
        public DisplaySignGump(Serial serial, int gumpID, string unknown, string caption) : base(0x8B)
        {
            if (unknown == null) unknown = "";
            if (caption == null) caption = "";

            this.EnsureCapacity(16 + unknown.Length + caption.Length);

            m_Stream.Write((int)serial);
            m_Stream.Write((short)gumpID);
            m_Stream.Write((short)(unknown.Length));
            m_Stream.WriteAsciiFixed(unknown, unknown.Length);
            m_Stream.Write((short)(caption.Length + 1));
            m_Stream.WriteAsciiFixed(caption, caption.Length + 1);
        }
    }

    public sealed class GodModeReply : Packet
    {
        public GodModeReply(bool reply) : base(0x2B, 2)
        {
            m_Stream.Write(reply);
        }
    }

    public sealed class PlayServerAck : Packet
    {
        internal static int m_AuthID = -1;

        public PlayServerAck(ServerInfo si) : base(0x8C, 11)
        {
            int addr = Utility.GetAddressValue(si.Address.Address);

            m_Stream.Write((byte)addr);
            m_Stream.Write((byte)(addr >> 8));
            m_Stream.Write((byte)(addr >> 16));
            m_Stream.Write((byte)(addr >> 24));

            m_Stream.Write((short)si.Address.Port);
            m_Stream.Write((int)m_AuthID);
        }
    }

    public abstract class Packet
    {
        [Flags]
        private enum State
        {
            Inactive = 0x00,
            Static = 0x01,
            Acquired = 0x02,
            Accessed = 0x04,
            Buffered = 0x08,
            Warned = 0x10
        }

        protected PacketWriter m_Stream;
        private int m_PacketID;
        private int m_Length;
        private State m_State;

        public int PacketID
        {
            get { return m_PacketID; }
        }

        protected Packet(int packetID)
        {
            m_PacketID = packetID;

            if (Core.Profiling)
            {
                PacketSendProfile prof = PacketSendProfile.Acquire(GetType());
                prof.Increment();
            }
        }

        public void EnsureCapacity(int length)
        {
            m_Stream = PacketWriter.CreateInstance(length);// new PacketWriter( length );
            m_Stream.Write((byte)m_PacketID);
            m_Stream.Write((short)0);
        }

        protected Packet(int packetID, int length)
        {
            m_PacketID = packetID;
            m_Length = length;

            m_Stream = PacketWriter.CreateInstance(length);// new PacketWriter( length );
            m_Stream.Write((byte)packetID);

            if (Core.Profiling)
            {
                PacketSendProfile prof = PacketSendProfile.Acquire(GetType());
                prof.Increment();
            }
        }

        public PacketWriter UnderlyingStream
        {
            get
            {
                return m_Stream;
            }
        }

        private const int CompressorBufferSize = 0x10000;
        private static BufferPool m_CompressorBuffers = new BufferPool("Compressor", 4, CompressorBufferSize);

        private const int BufferSize = 4096;
        private static BufferPool m_Buffers = new BufferPool("Compressed", 16, BufferSize);

        public static Packet SetStatic(Packet p)
        {
            p.SetStatic();
            return p;
        }

        public static Packet Acquire(Packet p)
        {
            p.Acquire();
            return p;
        }

        public static void Release(ref ObjectPropertyList p)
        {
            if (p != null)
                p.Release();

            p = null;
        }

        public static void Release(ref RemoveItem p)
        {
            if (p != null)
                p.Release();

            p = null;
        }

        public static void Release(ref RemoveMobile p)
        {
            if (p != null)
                p.Release();

            p = null;
        }

        public static void Release(ref OPLInfo p)
        {
            if (p != null)
                p.Release();

            p = null;
        }

        public static void Release(ref Packet p)
        {
            if (p != null)
                p.Release();

            p = null;
        }

        public static void Release(Packet p)
        {
            if (p != null)
                p.Release();
        }

        public void SetStatic()
        {
            m_State |= State.Static | State.Acquired;
        }

        public void Acquire()
        {
            m_State |= State.Acquired;
        }

        public void OnSend()
        {
            Core.Set();

            lock (this)
            {
                if ((m_State & (State.Acquired | State.Static)) == 0)
                    Free();
            }
        }

        private void Free()
        {
            if (m_CompiledBuffer == null)
                return;

            if ((m_State & State.Buffered) != 0)
                m_Buffers.ReleaseBuffer(m_CompiledBuffer);

            m_State &= ~(State.Static | State.Acquired | State.Buffered);

            m_CompiledBuffer = null;
        }

        public void Release()
        {
            if ((m_State & State.Acquired) != 0)
                Free();
        }

        private byte[] m_CompiledBuffer;
        private int m_CompiledLength;

        public byte[] Compile(bool compress, out int length)
        {
            lock (this)
            {
                if (m_CompiledBuffer == null)
                {
                    if ((m_State & State.Accessed) == 0)
                    {
                        m_State |= State.Accessed;
                    }
                    else
                    {
                        if ((m_State & State.Warned) == 0)
                        {
                            m_State |= State.Warned;

                            try
                            {
                                using (StreamWriter op = new StreamWriter("net_opt.log", true))
                                {
                                    op.WriteLine("Redundant compile for packet {0}, use Acquire() and Release()", this.GetType());
                                    op.WriteLine(new System.Diagnostics.StackTrace());
                                }
                            }
                            catch
                            {
                            }
                        }

                        m_CompiledBuffer = new byte[0];
                        m_CompiledLength = 0;

                        length = m_CompiledLength;
                        return m_CompiledBuffer;
                    }

                    InternalCompile(compress);
                }

                length = m_CompiledLength;
                return m_CompiledBuffer;
            }
        }

        private void InternalCompile(bool compress)
        {
            if (m_Length == 0)
            {
                long streamLen = m_Stream.Length;

                m_Stream.Seek(1, SeekOrigin.Begin);
                m_Stream.Write((ushort)streamLen);
            }
            else if (m_Stream.Length != m_Length)
            {
                int diff = (int)m_Stream.Length - m_Length;

                Console.WriteLine("Packet: 0x{0:X2}: Bad packet length! ({1}{2} bytes)", m_PacketID, diff >= 0 ? "+" : "", diff);
            }

            MemoryStream ms = m_Stream.UnderlyingStream;

            m_CompiledBuffer = ms.GetBuffer();
            int length = (int)ms.Length;

            if (compress)
            {
                byte[] buffer;
                lock (m_CompressorBuffers)
                    buffer = m_CompressorBuffers.AcquireBuffer();

                Compression.Compress(m_CompiledBuffer, 0, length, buffer, ref length);

                if (length <= 0)
                {
                    Console.WriteLine("Warning: Compression buffer overflowed on packet 0x{0:X2} ('{1}') (length={2})", m_PacketID, GetType().Name, length);
                    using (StreamWriter op = new StreamWriter("compression_overflow.log", true))
                    {
                        op.WriteLine("{0} Warning: Compression buffer overflowed on packet 0x{1:X2} ('{2}') (length={3})", DateTime.UtcNow, m_PacketID, GetType().Name, length);
                        op.WriteLine(new System.Diagnostics.StackTrace());
                    }
                }
                else
                {
                    m_CompiledLength = length;

                    if (length > BufferSize || (m_State & State.Static) != 0)
                    {
                        m_CompiledBuffer = new byte[length];
                    }
                    else
                    {
                        lock (m_Buffers)
                            m_CompiledBuffer = m_Buffers.AcquireBuffer();
                        m_State |= State.Buffered;
                    }

                    Buffer.BlockCopy(buffer, 0, m_CompiledBuffer, 0, length);

                    lock (m_CompressorBuffers)
                        m_CompressorBuffers.ReleaseBuffer(buffer);
                }
            }
            else if (length > 0)
            {
                byte[] old = m_CompiledBuffer;
                m_CompiledLength = length;

                if (length > BufferSize || (m_State & State.Static) != 0)
                {
                    m_CompiledBuffer = new byte[length];
                }
                else
                {
                    lock (m_Buffers)
                        m_CompiledBuffer = m_Buffers.AcquireBuffer();
                    m_State |= State.Buffered;
                }

                Buffer.BlockCopy(old, 0, m_CompiledBuffer, 0, length);
            }

            PacketWriter.ReleaseInstance(m_Stream);
            m_Stream = null;
        }
    }

    /// <summary>
    /// Provides functionality for writing primitive binary data.
    /// </summary>
    public class PacketWriter
    {
        private static Stack<PacketWriter> m_Pool = new Stack<PacketWriter>();

        public static PacketWriter CreateInstance()
        {
            return CreateInstance(32);
        }

        public static PacketWriter CreateInstance(int capacity)
        {
            PacketWriter pw = null;

            lock (m_Pool)
            {
                if (m_Pool.Count > 0)
                {
                    pw = m_Pool.Pop();

                    if (pw != null)
                    {
                        pw.m_Capacity = capacity;
                        pw.m_Stream.SetLength(0);
                    }
                }
            }

            if (pw == null)
                pw = new PacketWriter(capacity);

            return pw;
        }

        public static void ReleaseInstance(PacketWriter pw)
        {
            lock (m_Pool)
            {
                if (!m_Pool.Contains(pw))
                {
                    m_Pool.Push(pw);
                }
                else
                {
                    try
                    {
                        using (StreamWriter op = new StreamWriter("neterr.log"))
                        {
                            op.WriteLine("{0}\tInstance pool contains writer", DateTime.UtcNow);
                        }
                    }
                    catch
                    {
                        Console.WriteLine("net error");
                    }
                }
            }
        }

        /// <summary>
        /// Internal stream which holds the entire packet.
        /// </summary>
        private MemoryStream m_Stream;

        private int m_Capacity;

        /// <summary>
        /// Internal format buffer.
        /// </summary>
        private byte[] m_Buffer = new byte[4];

        /// <summary>
        /// Instantiates a new PacketWriter instance with the default capacity of 4 bytes.
        /// </summary>
        public PacketWriter() : this(32)
        {
        }

        /// <summary>
        /// Instantiates a new PacketWriter instance with a given capacity.
        /// </summary>
        /// <param name="capacity">Initial capacity for the internal stream.</param>
        public PacketWriter(int capacity)
        {
            m_Stream = new MemoryStream(capacity);
            m_Capacity = capacity;
        }

        /// <summary>
        /// Writes a 1-byte boolean value to the underlying stream. False is represented by 0, true by 1.
        /// </summary>
        public void Write(bool value)
        {
            m_Stream.WriteByte((byte)(value ? 1 : 0));
        }

        /// <summary>
        /// Writes a 1-byte unsigned integer value to the underlying stream.
        /// </summary>
        public void Write(byte value)
        {
            m_Stream.WriteByte(value);
        }

        /// <summary>
        /// Writes a 1-byte signed integer value to the underlying stream.
        /// </summary>
        public void Write(sbyte value)
        {
            m_Stream.WriteByte((byte)value);
        }

        /// <summary>
        /// Writes a 2-byte signed integer value to the underlying stream.
        /// </summary>
        public void Write(short value)
        {
            m_Buffer[0] = (byte)(value >> 8);
            m_Buffer[1] = (byte)value;

            m_Stream.Write(m_Buffer, 0, 2);
        }

        /// <summary>
        /// Writes a 2-byte unsigned integer value to the underlying stream.
        /// </summary>
        public void Write(ushort value)
        {
            m_Buffer[0] = (byte)(value >> 8);
            m_Buffer[1] = (byte)value;

            m_Stream.Write(m_Buffer, 0, 2);
        }

        /// <summary>
        /// Writes a 4-byte signed integer value to the underlying stream.
        /// </summary>
        public void Write(int value)
        {
            m_Buffer[0] = (byte)(value >> 24);
            m_Buffer[1] = (byte)(value >> 16);
            m_Buffer[2] = (byte)(value >> 8);
            m_Buffer[3] = (byte)value;

            m_Stream.Write(m_Buffer, 0, 4);
        }

        /// <summary>
        /// Writes a 4-byte unsigned integer value to the underlying stream.
        /// </summary>
        public void Write(uint value)
        {
            m_Buffer[0] = (byte)(value >> 24);
            m_Buffer[1] = (byte)(value >> 16);
            m_Buffer[2] = (byte)(value >> 8);
            m_Buffer[3] = (byte)value;

            m_Stream.Write(m_Buffer, 0, 4);
        }

        /// <summary>
        /// Writes a sequence of bytes to the underlying stream
        /// </summary>
        public void Write(byte[] buffer, int offset, int size)
        {
            m_Stream.Write(buffer, offset, size);
        }

        /// <summary>
        /// Writes a fixed-length ASCII-encoded string value to the underlying stream. To fit (size), the string content is either truncated or padded with null characters.
        /// </summary>
        public void WriteAsciiFixed(string value, int size)
        {
            if (value == null)
            {
                Console.WriteLine("Network: Attempted to WriteAsciiFixed() with null value");
                value = String.Empty;
            }

            int length = value.Length;

            m_Stream.SetLength(m_Stream.Length + size);

            if (length >= size)
                m_Stream.Position += Encoding.ASCII.GetBytes(value, 0, size, m_Stream.GetBuffer(), (int)m_Stream.Position);
            else
            {
                Encoding.ASCII.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
                m_Stream.Position += size;
            }

            /*byte[] buffer = Encoding.ASCII.GetBytes( value );

			if ( buffer.Length >= size )
			{
				m_Stream.Write( buffer, 0, size );
			}
			else
			{
				m_Stream.Write( buffer, 0, buffer.Length );
				Fill( size - buffer.Length );
			}*/
        }

        /// <summary>
        /// Writes a dynamic-length ASCII-encoded string value to the underlying stream, followed by a 1-byte null character.
        /// </summary>
        public void WriteAsciiNull(string value)
        {
            if (value == null)
            {
                Console.WriteLine("Network: Attempted to WriteAsciiNull() with null value");
                value = String.Empty;
            }

            int length = value.Length;

            m_Stream.SetLength(m_Stream.Length + length + 1);

            Encoding.ASCII.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
            m_Stream.Position += length + 1;

            /*byte[] buffer = Encoding.ASCII.GetBytes( value );

			m_Stream.Write( buffer, 0, buffer.Length );
			m_Stream.WriteByte( 0 );*/
        }

        /// <summary>
        /// Writes a dynamic-length little-endian unicode string value to the underlying stream, followed by a 2-byte null character.
        /// </summary>
        public void WriteLittleUniNull(string value)
        {
            if (value == null)
            {
                Console.WriteLine("Network: Attempted to WriteLittleUniNull() with null value");
                value = String.Empty;
            }

            int length = value.Length;

            m_Stream.SetLength(m_Stream.Length + ((length + 1) * 2));

            m_Stream.Position += Encoding.Unicode.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
            m_Stream.Position += 2;

            /*byte[] buffer = Encoding.Unicode.GetBytes( value );

			m_Stream.Write( buffer, 0, buffer.Length );

			m_Buffer[0] = 0;
			m_Buffer[1] = 0;
			m_Stream.Write( m_Buffer, 0, 2 );*/
        }

        /// <summary>
        /// Writes a fixed-length little-endian unicode string value to the underlying stream. To fit (size), the string content is either truncated or padded with null characters.
        /// </summary>
        public void WriteLittleUniFixed(string value, int size)
        {
            if (value == null)
            {
                Console.WriteLine("Network: Attempted to WriteLittleUniFixed() with null value");
                value = String.Empty;
            }

            size *= 2;

            int length = value.Length;

            m_Stream.SetLength(m_Stream.Length + size);

            if ((length * 2) >= size)
                m_Stream.Position += Encoding.Unicode.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
            else
            {
                Encoding.Unicode.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
                m_Stream.Position += size;
            }

            /*size *= 2;

			byte[] buffer = Encoding.Unicode.GetBytes( value );

			if ( buffer.Length >= size )
			{
				m_Stream.Write( buffer, 0, size );
			}
			else
			{
				m_Stream.Write( buffer, 0, buffer.Length );
				Fill( size - buffer.Length );
			}*/
        }

        /// <summary>
        /// Writes a dynamic-length big-endian unicode string value to the underlying stream, followed by a 2-byte null character.
        /// </summary>
        public void WriteBigUniNull(string value)
        {
            if (value == null)
            {
                Console.WriteLine("Network: Attempted to WriteBigUniNull() with null value");
                value = String.Empty;
            }

            int length = value.Length;

            m_Stream.SetLength(m_Stream.Length + ((length + 1) * 2));

            m_Stream.Position += Encoding.BigEndianUnicode.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
            m_Stream.Position += 2;

            /*byte[] buffer = Encoding.BigEndianUnicode.GetBytes( value );

			m_Stream.Write( buffer, 0, buffer.Length );

			m_Buffer[0] = 0;
			m_Buffer[1] = 0;
			m_Stream.Write( m_Buffer, 0, 2 );*/
        }

        /// <summary>
        /// Writes a fixed-length big-endian unicode string value to the underlying stream. To fit (size), the string content is either truncated or padded with null characters.
        /// </summary>
        public void WriteBigUniFixed(string value, int size)
        {
            if (value == null)
            {
                Console.WriteLine("Network: Attempted to WriteBigUniFixed() with null value");
                value = String.Empty;
            }

            size *= 2;

            int length = value.Length;

            m_Stream.SetLength(m_Stream.Length + size);

            if ((length * 2) >= size)
                m_Stream.Position += Encoding.BigEndianUnicode.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
            else
            {
                Encoding.BigEndianUnicode.GetBytes(value, 0, length, m_Stream.GetBuffer(), (int)m_Stream.Position);
                m_Stream.Position += size;
            }

            /*size *= 2;

			byte[] buffer = Encoding.BigEndianUnicode.GetBytes( value );

			if ( buffer.Length >= size )
			{
				m_Stream.Write( buffer, 0, size );
			}
			else
			{
				m_Stream.Write( buffer, 0, buffer.Length );
				Fill( size - buffer.Length );
			}*/
        }

        /// <summary>
        /// Fills the stream from the current position up to (capacity) with 0x00's
        /// </summary>
        public void Fill()
        {
            Fill((int)(m_Capacity - m_Stream.Length));
        }

        /// <summary>
        /// Writes a number of 0x00 byte values to the underlying stream.
        /// </summary>
        public void Fill(int length)
        {
            if (m_Stream.Position == m_Stream.Length)
            {
                m_Stream.SetLength(m_Stream.Length + length);
                m_Stream.Seek(0, SeekOrigin.End);
            }
            else
            {
                m_Stream.Write(new byte[length], 0, length);
            }
        }

        /// <summary>
        /// Gets the total stream length.
        /// </summary>
        public long Length
        {
            get
            {
                return m_Stream.Length;
            }
        }

        /// <summary>
        /// Gets or sets the current stream position.
        /// </summary>
        public long Position
        {
            get
            {
                return m_Stream.Position;
            }
            set
            {
                m_Stream.Position = value;
            }
        }

        /// <summary>
        /// The internal stream used by this PacketWriter instance.
        /// </summary>
        public MemoryStream UnderlyingStream
        {
            get
            {
                return m_Stream;
            }
        }

        /// <summary>
        /// Offsets the current position from an origin.
        /// </summary>
        public long Seek(long offset, SeekOrigin origin)
        {
            return m_Stream.Seek(offset, origin);
        }

        /// <summary>
        /// Gets the entire stream content as a byte array.
        /// </summary>
        public byte[] ToArray()
        {
            return m_Stream.ToArray();
        }
    }

    public class SendQueue
    {
        public class Gram
        {
            private static Stack<Gram> _pool = new Stack<Gram>();

            public static Gram Acquire()
            {
                lock (_pool)
                {
                    Gram gram;

                    if (_pool.Count > 0)
                    {
                        gram = _pool.Pop();
                    }
                    else
                    {
                        gram = new Gram();
                    }

                    gram._buffer = AcquireBuffer();
                    gram._length = 0;

                    return gram;
                }
            }

            private byte[] _buffer;
            private int _length;

            public byte[] Buffer
            {
                get
                {
                    return _buffer;
                }
            }

            public int Length
            {
                get
                {
                    return _length;
                }
            }

            public int Available
            {
                get
                {
                    return (_buffer.Length - _length);
                }
            }

            public bool IsFull
            {
                get
                {
                    return (_length == _buffer.Length);
                }
            }

            private Gram()
            {
            }

            public int Write(byte[] buffer, int offset, int length)
            {
                int write = Math.Min(length, this.Available);

                System.Buffer.BlockCopy(buffer, offset, _buffer, _length, write);

                _length += write;

                return write;
            }

            public void Release()
            {
                lock (_pool)
                {
                    _pool.Push(this);
                    ReleaseBuffer(_buffer);
                }
            }
        }

        private static int m_CoalesceBufferSize = 512;
        private static BufferPool m_UnusedBuffers = new BufferPool("Coalesced", 2048, m_CoalesceBufferSize);

        public static int CoalesceBufferSize
        {
            get
            {
                return m_CoalesceBufferSize;
            }
            set
            {
                if (m_CoalesceBufferSize == value)
                    return;

                BufferPool old = m_UnusedBuffers;

                lock (old)
                {
                    if (m_UnusedBuffers != null)
                        m_UnusedBuffers.Free();

                    m_CoalesceBufferSize = value;
                    m_UnusedBuffers = new BufferPool("Coalesced", 2048, m_CoalesceBufferSize);
                }
            }
        }

        public static byte[] AcquireBuffer()
        {
            lock (m_UnusedBuffers)
                return m_UnusedBuffers.AcquireBuffer();
        }

        public static void ReleaseBuffer(byte[] buffer)
        {
            lock (m_UnusedBuffers)
                if (buffer != null && buffer.Length == m_CoalesceBufferSize)
                    m_UnusedBuffers.ReleaseBuffer(buffer);
        }

        private Queue<Gram> _pending;

        private Gram _buffered;

        public bool IsFlushReady
        {
            get
            {
                return (_pending.Count == 0 && _buffered != null);
            }
        }

        public bool IsEmpty
        {
            get
            {
                return (_pending.Count == 0 && _buffered == null);
            }
        }

        public SendQueue()
        {
            _pending = new Queue<Gram>();
        }

        public Gram CheckFlushReady()
        {
            Gram gram = _buffered;
            _pending.Enqueue(_buffered);
            _buffered = null;
            return gram;
        }

        public Gram Dequeue()
        {
            Gram gram = null;

            if (_pending.Count > 0)
            {
                _pending.Dequeue().Release();

                if (_pending.Count > 0)
                {
                    gram = _pending.Peek();
                }
            }

            return gram;
        }

        private const int PendingCap = 256 * 1024;

        public Gram Enqueue(byte[] buffer, int length)
        {
            return Enqueue(buffer, 0, length);
        }

        public Gram Enqueue(byte[] buffer, int offset, int length)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            else if (!(offset >= 0 && offset < buffer.Length))
            {
                throw new ArgumentOutOfRangeException("offset", offset, "Offset must be greater than or equal to zero and less than the size of the buffer.");
            }
            else if (length < 0 || length > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("length", length, "Length cannot be less than zero or greater than the size of the buffer.");
            }
            else if ((buffer.Length - offset) < length)
            {
                throw new ArgumentException("Offset and length do not point to a valid segment within the buffer.");
            }

            int existingBytes = (_pending.Count * m_CoalesceBufferSize) + (_buffered == null ? 0 : _buffered.Length);

            if ((existingBytes + length) > PendingCap)
            {
                throw new CapacityExceededException();
            }

            Gram gram = null;

            while (length > 0)
            {
                if (_buffered == null)
                { // nothing yet buffered
                    _buffered = Gram.Acquire();
                }

                int bytesWritten = _buffered.Write(buffer, offset, length);

                offset += bytesWritten;
                length -= bytesWritten;

                if (_buffered.IsFull)
                {
                    if (_pending.Count == 0)
                    {
                        gram = _buffered;
                    }

                    _pending.Enqueue(_buffered);
                    _buffered = null;
                }
            }

            return gram;
        }

        public void Clear()
        {
            if (_buffered != null)
            {
                _buffered.Release();
                _buffered = null;
            }

            while (_pending.Count > 0)
            {
                _pending.Dequeue().Release();
            }
        }
    }

    [Serializable]
    public sealed class CapacityExceededException : Exception
    {
        public CapacityExceededException()
            : base("Too much data pending.")
        {
        }
    }
}