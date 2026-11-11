using System;
using System.Collections.Generic;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Hard-asserts that the public enums match the official Informix.Net.Core provider
/// name-for-name and value-for-value (the tables from the compat gap analysis).
/// </summary>
public class EnumParityTests
{
    private static void AssertEnumParity<TEnum>(
        Dictionary<string, long> expected, Dictionary<string, long>? intentionalAdditions = null) where TEnum : struct, Enum
    {
        foreach (var (name, value) in expected)
        {
            Assert.True(Enum.IsDefined(typeof(TEnum), name), $"{typeof(TEnum).Name}.{name} is missing.");
            Assert.Equal(value, Convert.ToInt64(Enum.Parse<TEnum>(name)));
        }
        foreach (var (name, value) in intentionalAdditions ?? [])
        {
            Assert.True(Enum.IsDefined(typeof(TEnum), name), $"{typeof(TEnum).Name}.{name} is missing.");
            Assert.Equal(value, Convert.ToInt64(Enum.Parse<TEnum>(name)));
        }
        // No extra members beyond the official set plus the explicitly-documented additions.
        Assert.Equal(expected.Count + (intentionalAdditions?.Count ?? 0), Enum.GetNames<TEnum>().Length);
    }

    [Fact]
    public void IfxType_matches_the_official_provider()
    {
        AssertEnumParity<IfxType>(
            new()
            {
                ["BigInt"] = 1, ["Binary"] = 2, ["Bit"] = 3, ["Char"] = 4, ["DateTime"] = 5, ["Decimal"] = 6,
                ["Numeric"] = 7, ["Double"] = 8, ["Image"] = 9, ["Int"] = 10, ["NChar"] = 11, ["NText"] = 12,
                ["NVarChar"] = 13, ["Real"] = 14, ["UniqueIdentifier"] = 15, ["SmallDateTime"] = 16,
                ["SmallInt"] = 17, ["Text"] = 18, ["Timestamp"] = 19, ["TinyInt"] = 20, ["VarBinary"] = 21,
                ["VarChar"] = 22, ["Date"] = 23, ["Time"] = 24, ["Integer"] = 25, ["Float"] = 26,
                ["SmallFloat"] = 27, ["Serial"] = 28, ["Money"] = 29, ["Null"] = 30, ["Byte"] = 31, ["Int8"] = 32,
                ["Serial8"] = 33, ["Set"] = 34, ["MultiSet"] = 35, ["List"] = 36, ["Row"] = 37, ["SQLUDTVar"] = 40,
                ["SQLUDTFixed"] = 41, ["BigSerial"] = 53, ["Other"] = 99, ["LVarChar"] = 101, ["Blob"] = 110,
                ["Clob"] = 111, ["SmartLOBLocator"] = 112, ["Boolean"] = 126, ["Char1"] = 1001,
                ["IntervalYearMonth"] = 1400, ["IntervalDayFraction"] = 1499,
            },
            // JSON/BSON have no counterpart in the official provider (verified against
            // InformixNetCore_official_api.txt) — a deliberate driver enhancement, not drift.
            // BinaryVar/Binary18 name the two Database Extensions binary UDTs so a byte[] can be bound to them
            // (a plain byte[] binds as BYTE, which the server cannot cast to either).
            intentionalAdditions: new() { ["Json"] = 113, ["Bson"] = 114, ["BinaryVar"] = 115, ["Binary18"] = 116 });
    }

    [Fact]
    public void IfxTimeUnit_matches_the_official_provider()
    {
        AssertEnumParity<IfxTimeUnit>(new()
        {
            ["Year"] = 0, ["Month"] = 2, ["Day"] = 4, ["Hour"] = 6, ["Minute"] = 8, ["Second"] = 10,
            ["Fraction1"] = 11, ["Fraction2"] = 12, ["Fraction3"] = 13, ["Fraction4"] = 14,
            ["Fraction5"] = 15, ["Fraction"] = 16,
        });
    }

    [Fact]
    public void IfxSmartLOBWhence_matches_the_official_provider() =>
        AssertEnumParity<IfxSmartLOBWhence>(new() { ["Begin"] = 0, ["Current"] = 1, ["End"] = 2 });

    [Fact]
    public void IfxSmartLOBOpenMode_matches_the_official_provider() =>
        AssertEnumParity<IfxSmartLOBOpenMode>(new()
        {
            ["None"] = 0, ["Append"] = 1, ["WriteOnly"] = 2, ["ReadOnly"] = 4, ["ReadWrite"] = 8,
            ["DirtyRead"] = 16, ["Buffer"] = 512, ["Nobuffer"] = 1024, ["LockAll"] = 4096, ["LockRange"] = 8192,
        });

    [Fact]
    public void IfxSmartLOBLockMode_matches_the_official_provider() =>
        AssertEnumParity<IfxSmartLOBLockMode>(new() { ["None"] = 0, ["Shared"] = 1, ["Exclusive"] = 2 });

    [Fact]
    public void IfxSmartLOBFileLocation_matches_the_official_provider() =>
        AssertEnumParity<IfxSmartLOBFileLocation>(new() { ["None"] = 0, ["Server"] = 1, ["Client"] = 2 });

    [Fact]
    public void IfxSmartLOBCreateTimeFlags_matches_the_official_provider() =>
        AssertEnumParity<IfxSmartLOBCreateTimeFlags>(new()
        {
            ["None"] = 0, ["Log"] = 1, ["NoLog"] = 2, ["KeepAccessTime"] = 8, ["DontKeepAccessTime"] = 16,
        });
}
