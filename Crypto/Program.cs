using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

// Same ChaCha20 (counter=1, 96-bit nonce) as the vendored VFS reader.
if (args.Length != 2) return 2;
var raw = File.ReadAllBytes(args[0]);
if (raw.Length < 12) throw new InvalidDataException("Short VFS index.");
var key = Convert.FromHexString("E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1");
uint[] initial = [0x61707865, 0x3320646E, 0x79622D32, 0x6B206574, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0];
for (int i = 0; i < 8; i++) initial[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
for (int i = 0; i < 3; i++) initial[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
var output = new byte[raw.Length - 12];
Span<uint> working = stackalloc uint[16];
Span<byte> stream = stackalloc byte[64];
for (int offset = 0; offset < output.Length; offset += 64)
{
    initial.CopyTo(working);
    for (int round = 0; round < 10; round++)
    {
        Quarter(working, 0, 4, 8, 12); Quarter(working, 1, 5, 9, 13);
        Quarter(working, 2, 6, 10, 14); Quarter(working, 3, 7, 11, 15);
        Quarter(working, 0, 5, 10, 15); Quarter(working, 1, 6, 11, 12);
        Quarter(working, 2, 7, 8, 13); Quarter(working, 3, 4, 9, 14);
    }
    for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(stream[(i * 4)..], unchecked(working[i] + initial[i]));
    int count = Math.Min(64, output.Length - offset);
    for (int i = 0; i < count; i++) output[offset + i] = (byte)(raw[offset + i + 12] ^ stream[i]);
    initial[12]++;
}
File.WriteAllBytes(args[1], output);
return 0;

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static void Quarter(Span<uint> s, int a, int b, int c, int d)
{
    s[a] = unchecked(s[a] + s[b]); s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
    s[c] = unchecked(s[c] + s[d]); s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
    s[a] = unchecked(s[a] + s[b]); s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
    s[c] = unchecked(s[c] + s[d]); s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
}
