using System;
using Informix.Data.Sqli;
using Informix.Data.Wire;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxSmartLOB.ReferenceCount"/>, <see cref="IfxSmartLOB.LastAccessTime"/>,
/// <see cref="IfxSmartLOB.LastChangeTime"/> and <see cref="IfxSmartLOB.LastModificationTime"/>
/// read from the IFX_LO_STAT block, as Informix.Net.Core does (its getters call
/// GetLoStatusInfo). The offsets are JDBC IfxLoStat's field order, confirmed live by changing
/// one thing at a time (see SqliLobStat).
/// </summary>
public class SmartLobStatTests
{
    private static byte[] Locator(byte seed)
    {
        var l = new byte[72];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)(seed + i);
        return l;
    }

    // A stat block whose every field holds a different value, so a swapped offset shows.
    private static byte[] StatBlock(int refCount, int accessTime, int modifyTime, int statusTime, long size = 5000)
    {
        var b = new byte[644];
        IfxWireConverter.WriteInt32(b.AsSpan(0, 4), unchecked((int)0xFEDABABE));
        SqliSession.WriteUdtLong(b.AsSpan(4), size);
        IfxWireConverter.WriteInt32(b.AsSpan(16, 4), 0);              // lobUID
        IfxWireConverter.WriteInt32(b.AsSpan(20, 4), accessTime);
        IfxWireConverter.WriteInt32(b.AsSpan(24, 4), modifyTime);
        IfxWireConverter.WriteInt32(b.AsSpan(28, 4), 123_456);        // modify time, microseconds part
        IfxWireConverter.WriteInt32(b.AsSpan(32, 4), statusTime);
        IfxWireConverter.WriteInt32(b.AsSpan(36, 4), refCount);
        SqliSession.BuildDefaultLobDescriptor().CopyTo(b, 40);
        return b;
    }

    // ifx_lo_open (handle + fd), then the first ifx_lo_stat (handle + block); later stats reuse
    // the cached routine handle, so each costs only the call itself.
    private static byte[] OpenThenStats(params byte[][] stats)
    {
        var frames = new System.Collections.Generic.List<byte[]>
        {
            SqliStatementKit.RoutineHandleFrame("db", 67),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.FpReturnFrame(39),
            SqliStatementKit.EotFrame(),
            SqliStatementKit.RoutineHandleFrame("db", 69),
            SqliStatementKit.EotFrame(),
        };
        foreach (byte[] stat in stats)
        {
            frames.Add(SqliStatementKit.FpReturnFrame(stat));
            frames.Add(SqliStatementKit.EotFrame());
        }
        return SqliStatementKit.Join(frames.ToArray());
    }

    [Fact]
    public void Each_property_reads_its_own_field_of_the_stat_block()
    {
        byte[] stat = StatBlock(refCount: 2, accessTime: 1_794_418_658, modifyTime: 1_794_418_651, statusTime: 1_794_418_656);
        using var cn = SqliStatementKit.OpenConnection(OpenThenStats(stat, stat, stat, stat), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        blob.Open(IfxSmartLOBOpenMode.ReadOnly);

        Assert.Equal(2, blob.ReferenceCount);
        Assert.Equal(1_794_418_658, blob.LastAccessTime);
        Assert.Equal(1_794_418_651, blob.LastModificationTime);
        Assert.Equal(1_794_418_656, blob.LastChangeTime);
    }

    [Fact]
    public void Every_read_asks_the_server_so_the_value_is_current()
    {
        using var cn = SqliStatementKit.OpenConnection(
            OpenThenStats(StatBlock(1, 10, 10, 10), StatBlock(2, 10, 10, 20)), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        blob.Open(IfxSmartLOBOpenMode.ReadOnly);

        Assert.Equal(1, blob.ReferenceCount);   // a row was inserted between the two reads
        Assert.Equal(2, blob.ReferenceCount);
    }

    [Fact]
    public void The_size_still_comes_from_the_same_block()
    {
        using var cn = SqliStatementKit.OpenConnection(OpenThenStats(StatBlock(0, 1, 1, 1, size: 300_000)), out _);
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));
        blob.Open(IfxSmartLOBOpenMode.ReadOnly);

        Assert.Equal(300_000, blob.Size);
    }

    [Fact]
    public void An_object_that_is_not_open_says_so_without_touching_the_server()
    {
        using var cn = SqliStatementKit.OpenConnection([], out var duplex);
        long before = duplex.Output.Length;
        var blob = new IfxBlob(cn, new IfxSmartLOBLocator(Locator(0x10)));

        var ex = Assert.Throws<InvalidOperationException>(() => blob.ReferenceCount);

        Assert.Contains("not open", ex.Message);
        Assert.Equal(before, duplex.Output.Length);
    }

    [Fact]
    public void An_object_not_yet_created_says_that_Open_creates_it()
    {
        using var cn = SqliStatementKit.OpenConnection([], out _);

        var ex = Assert.Throws<InvalidOperationException>(() => cn.GetIfxClob().LastModificationTime);

        Assert.Contains("Open() creates it", ex.Message);
    }

    [Fact]
    public void The_SQL_NULL_sentinel_has_no_status()
    {
        Assert.Throws<InvalidOperationException>(() => IfxBlob.Null.ReferenceCount);
        Assert.Throws<InvalidOperationException>(() => IfxClob.Null.LastAccessTime);
    }
}
