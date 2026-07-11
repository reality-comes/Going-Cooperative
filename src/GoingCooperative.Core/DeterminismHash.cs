using System;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public struct DeterminismHash
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        private ulong value;

        public DeterminismHash(ulong seed)
        {
            value = seed == 0 ? OffsetBasis : seed;
        }

        public ulong Value
        {
            get { return value == 0 ? OffsetBasis : value; }
        }

        public void Add(string text)
        {
            if (text == null)
            {
                Add(-1);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            Add(bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
            {
                AddByte(bytes[i]);
            }
        }

        public void AddBytes(byte[] data, int count)
        {
            if (data == null)
            {
                Add(-1);
                return;
            }

            Add(count);
            for (var i = 0; i < count; i++)
            {
                AddByte(data[i]);
            }
        }

        public void Add(int number)
        {
            Add(unchecked((ulong)number));
        }

        public void Add(long number)
        {
            Add(unchecked((ulong)number));
        }

        public void Add(ulong number)
        {
            AddByte((byte)(number & 0xff));
            AddByte((byte)((number >> 8) & 0xff));
            AddByte((byte)((number >> 16) & 0xff));
            AddByte((byte)((number >> 24) & 0xff));
            AddByte((byte)((number >> 32) & 0xff));
            AddByte((byte)((number >> 40) & 0xff));
            AddByte((byte)((number >> 48) & 0xff));
            AddByte((byte)((number >> 56) & 0xff));
        }

        public void Add(double number)
        {
            Add(BitConverter.DoubleToInt64Bits(number));
        }

        public static ulong HashString(string text)
        {
            var hash = new DeterminismHash();
            hash.Add(text ?? string.Empty);
            return hash.Value;
        }

        public static string Format(ulong hash)
        {
            return "0x" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private void AddByte(byte data)
        {
            if (value == 0)
            {
                value = OffsetBasis;
            }

            value ^= data;
            value *= Prime;
        }
    }
}
