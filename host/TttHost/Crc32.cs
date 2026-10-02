namespace TttHost;

/// <summary>Standard CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320) as used by zip and MAME. No external package.</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _state = 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        uint c = _state;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _state = c;
    }

    public uint Value => ~_state;

    public static uint Of(ReadOnlySpan<byte> data)
    {
        var c = new Crc32();
        c.Append(data);
        return c.Value;
    }
}
