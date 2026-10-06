using System.Security.Cryptography;
using System.Text;

namespace Verita.Domain;

/// <summary>
/// Identifier generation. Rows the platform creates get sequential GUIDs, ordered the way SQL
/// Server sorts UNIQUEIDENTIFIER (the last six bytes compare first), so result tables do not
/// fragment their indexes the way random GUIDs would (BRD section 18, type decisions).
/// </summary>
public static class IdGen
{
    private static long _last;
    private static readonly object Gate = new();

    public static Guid Sequential()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        long ticks;
        lock (Gate)
        {
            ticks = Math.Max(DateTime.UtcNow.Ticks, _last + 1);
            _last = ticks;
        }
        var t = BitConverter.GetBytes(ticks);
        if (BitConverter.IsLittleEndian) Array.Reverse(t);
        // SQL Server orders by bytes 10..15 first, most significant at 10.
        Array.Copy(t, 2, bytes, 10, 6);
        return new Guid(bytes);
    }

    /// <summary>
    /// A stable GUID derived from an external key, so re-importing the same fixture yields the
    /// same identifiers (idempotent load on the natural key, BRD 22.2).
    /// </summary>
    public static Guid FromKey(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("verita:" + key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3 (name-based)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }
}
