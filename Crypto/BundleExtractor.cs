using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

internal static class BundleExtractor
{
    public static int Execute(string[] args)
    {
        if (args.Length != 8) throw new ArgumentException("--extract chunk offset length version|- seed|- md5 output");
        long offset = long.Parse(args[2], CultureInfo.InvariantCulture);
        long length = long.Parse(args[3], CultureInfo.InvariantCulture);
        var expected = Convert.FromHexString(args[6]);
        if (expected.Length != 16 || offset < 0 || length <= 0 || length > 64L * uint.MaxValue)
            throw new InvalidDataException("Invalid Bundle range or MD5.");
        using var source = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset > source.Length || length > source.Length - offset) throw new InvalidDataException("Incomplete CHK file.");
        source.Position = offset;
        uint[]? state = null;
        if (args[4] != "-")
        {
            var key = Convert.FromHexString("E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1");
            state = [0x61707865, 0x3320646E, 0x79622D32, 0x6B206574, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                uint.Parse(args[4], CultureInfo.InvariantCulture), 0, 0];
            for (int index = 0; index < 8; index++) state[4 + index] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(index * 4));
            ulong seed = ulong.Parse(args[5], CultureInfo.InvariantCulture);
            state[14] = (uint)seed;
            state[15] = (uint)(seed >> 32);
        }
        using var output = new FileStream(args[7], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1024 * 1024];
        long completed = 0;
        while (completed < length)
        {
            int count = (int)Math.Min(buffer.Length, length - completed);
            source.ReadExactly(buffer.AsSpan(0, count));
            if (state != null) Transform(buffer.AsSpan(0, count), state);
            hash.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
            completed += count;
            if (completed % (8 * 1024 * 1024) == 0 || completed == length) Console.WriteLine("@bytes " + completed);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
            throw new InvalidDataException("Bundle MD5 mismatch.");
        output.Flush(flushToDisk: true);
        return 0;
    }

    private static void Transform(Span<byte> data, uint[] initial)
    {
        Span<uint> working = stackalloc uint[16];
        Span<byte> stream = stackalloc byte[64];
        for (int offset = 0; offset < data.Length; offset += 64)
        {
            initial.CopyTo(working);
            for (int round = 0; round < 10; round++)
            {
                Quarter(working, 0, 4, 8, 12); Quarter(working, 1, 5, 9, 13);
                Quarter(working, 2, 6, 10, 14); Quarter(working, 3, 7, 11, 15);
                Quarter(working, 0, 5, 10, 15); Quarter(working, 1, 6, 11, 12);
                Quarter(working, 2, 7, 8, 13); Quarter(working, 3, 4, 9, 14);
            }
            for (int index = 0; index < 16; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(stream[(index * 4)..], unchecked(working[index] + initial[index]));
            for (int index = 0; index < Math.Min(64, data.Length - offset); index++) data[offset + index] ^= stream[index];
            initial[12]++;
        }
    }

    private static void Quarter(Span<uint> state, int a, int b, int c, int d)
    {
        state[a] = unchecked(state[a] + state[b]); state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 16);
        state[c] = unchecked(state[c] + state[d]); state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 12);
        state[a] = unchecked(state[a] + state[b]); state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 8);
        state[c] = unchecked(state[c] + state[d]); state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 7);
    }
}
