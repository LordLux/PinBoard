namespace PinBoard.Helpers;

using PinBoard.Models;

/// Simple binary serializer for FormatBundle.
///
/// Format (little-endian):
///   [4 bytes] entry count
///   for each entry:
///     [4 bytes] key byte count (UTF-8)
///     [N bytes] key
///     [4 bytes] value byte count
///     [N bytes] value
internal static class BundleSerializer
{
    public static byte[] Serialize(FormatBundle bundle)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        w.Write(bundle.Formats.Count);
        foreach (var (key, value) in bundle.Formats)
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
            w.Write(keyBytes.Length);
            w.Write(keyBytes);
            w.Write(value.Length);
            w.Write(value);
        }

        return ms.ToArray();
    }

    public static FormatBundle Deserialize(byte[] data)
    {
        var bundle = new FormatBundle();
        using var ms = new MemoryStream(data);
        using var r  = new BinaryReader(ms);

        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            int  keyLen   = r.ReadInt32();
            var  keyBytes = r.ReadBytes(keyLen);
            int  valLen   = r.ReadInt32();
            var  valBytes = r.ReadBytes(valLen);

            bundle.Formats[System.Text.Encoding.UTF8.GetString(keyBytes)] = valBytes;
        }

        return bundle;
    }
}
