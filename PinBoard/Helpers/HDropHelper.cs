using System.Text;

namespace PinBoard.Helpers;

/// Parses a CF_HDROP byte payload into a list of file paths.
internal static class HDropHelper
{
    public static IReadOnlyList<string> ParseFiles(byte[] hdropBytes)
    {
        // DROPFILES: pFiles(4) + POINT(8) + fNC(4) + fWide(4) = 20 bytes
        if (hdropBytes.Length < 20) return [];

        uint pFiles = BitConverter.ToUInt32(hdropBytes, 0);
        bool fWide  = BitConverter.ToInt32(hdropBytes, 16) != 0;
        int  offset = (int)pFiles;

        var files = new List<string>();

        if (fWide)
        {
            while (offset + 2 <= hdropBytes.Length)
            {
                int end = offset;
                while (end + 1 < hdropBytes.Length &&
                       !(hdropBytes[end] == 0 && hdropBytes[end + 1] == 0))
                    end += 2;

                if (end == offset) break; // double-null terminator

                files.Add(Encoding.Unicode.GetString(hdropBytes, offset, end - offset));
                offset = end + 2;
            }
        }
        else
        {
            while (offset < hdropBytes.Length)
            {
                int end = offset;
                while (end < hdropBytes.Length && hdropBytes[end] != 0)
                    end++;

                if (end == offset) break; // double-null terminator

                files.Add(Encoding.Default.GetString(hdropBytes, offset, end - offset));
                offset = end + 1;
            }
        }

        return files;
    }
}
