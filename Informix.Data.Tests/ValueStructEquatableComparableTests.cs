using System;
using System.Collections.Generic;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IEquatable{T}"/>/<see cref="IComparable{T}"/> parity for the four nullable
/// value structs (<see cref="IfxDecimal"/>, <see cref="IfxDateTime"/>, <see cref="IfxTimeSpan"/>,
/// <see cref="IfxMonthSpan"/>): the typed and object-based paths must never diverge, and the
/// existing SQL-NULL semantics -- NULL is never equal to anything (including another NULL), but
/// NULL sorts first via <see cref="IComparable{T}"/> -- must survive through the generic
/// interfaces unchanged, not just through the object-based ones.
/// </summary>
public class ValueStructEquatableComparableTests
{
    [Fact]
    public void IfxDecimal_typed_and_object_paths_agree() =>
        AssertParity(new IfxDecimal(1m), new IfxDecimal(2m), IfxDecimal.Null, static x => x.IsNull);

    [Fact]
    public void IfxDateTime_typed_and_object_paths_agree() =>
        AssertParity(new IfxDateTime(new DateTime(2026, 1, 1)), new IfxDateTime(new DateTime(2026, 6, 1)), IfxDateTime.Null, static x => x.IsNull);

    [Fact]
    public void IfxTimeSpan_typed_and_object_paths_agree() =>
        AssertParity(new IfxTimeSpan(TimeSpan.FromMinutes(1)), new IfxTimeSpan(TimeSpan.FromHours(1)), IfxTimeSpan.Null, static x => x.IsNull);

    [Fact]
    public void IfxMonthSpan_typed_and_object_paths_agree() =>
        AssertParity(new IfxMonthSpan(1, IfxTimeUnit.Year), new IfxMonthSpan(2, IfxTimeUnit.Year), IfxMonthSpan.Null, static x => x.IsNull);

    /// <summary>
    /// Runs the full parity matrix for one value-struct type: <paramref name="small"/> and
    /// <paramref name="big"/> must be two distinct non-NULL values with <c>small &lt; big</c>;
    /// <paramref name="nullValue"/> must be that type's SQL NULL. <paramref name="isNull"/> reads
    /// the type's own <c>IsNull</c> property -- needed because <see cref="Equals(object)"/>
    /// can't be used to identify the NULL slot after sorting (SQL NULL is never "Equal", not even
    /// to another NULL, so <c>Assert.Equal(nullValue, ...)</c> would itself fail).
    /// </summary>
    private static void AssertParity<T>(T small, T big, T nullValue, Func<T, bool> isNull)
        where T : struct, IEquatable<T>, IComparable<T>
    {
        // typed Equals == object Equals: (value,value), (value,Null), (Null,Null)
        Assert.True(small.Equals(small));
        Assert.Equal(small.Equals(small), small.Equals((object)small));

        Assert.False(small.Equals(nullValue));
        Assert.Equal(small.Equals(nullValue), small.Equals((object)nullValue));

        Assert.False(nullValue.Equals(nullValue)); // SQL NULL is never equal, even to itself
        Assert.Equal(nullValue.Equals(nullValue), nullValue.Equals((object)nullValue));

        // hash consistency
        Assert.Equal(small.GetHashCode(), ((object)small).GetHashCode());
        Assert.Equal(0, nullValue.GetHashCode());
        Assert.Equal(nullValue.GetHashCode(), ((object)nullValue).GetHashCode());

        // typed CompareTo agrees with the object-based IComparable path
        Assert.True(small.CompareTo(big) < 0);
        Assert.True(big.CompareTo(small) > 0);
        Assert.Equal(0, small.CompareTo(small));
        Assert.Equal(Math.Sign(small.CompareTo(big)), Math.Sign(((IComparable)small).CompareTo(big)));
        Assert.Equal(Math.Sign(big.CompareTo(small)), Math.Sign(((IComparable)big).CompareTo(small)));

        // NULL sorts first (the SqlDecimal convention), agreeing on both paths
        Assert.True(nullValue.CompareTo(small) < 0);
        Assert.True(small.CompareTo(nullValue) > 0);
        Assert.Equal(0, nullValue.CompareTo(nullValue));
        Assert.Equal(Math.Sign(nullValue.CompareTo(small)), Math.Sign(((IComparable)nullValue).CompareTo(small)));

        // List<T>.Sort() (Comparer<T>.Default, which prefers IComparable<T>) places NULL first
        var list = new List<T> { big, small, nullValue };
        list.Sort();
        Assert.True(isNull(list[0]));
        Assert.Equal(small, list[1]);
        Assert.Equal(big, list[2]);

        // EqualityComparer<T>.Default uses the typed IEquatable<T> path (no boxing)
        Assert.True(EqualityComparer<T>.Default.Equals(small, small));
        Assert.False(EqualityComparer<T>.Default.Equals(small, big));
        Assert.False(EqualityComparer<T>.Default.Equals(nullValue, nullValue));

        // A `where T : IEquatable<T>` generic helper compiles and passes -- proves the
        // interface is genuinely implemented, not just shape-matched.
        Assert.True(GenericEquals(small, small));
        Assert.False(GenericEquals(small, big));
        Assert.False(GenericEquals(nullValue, nullValue));

        // IComparable<T> path agrees with the object-based IComparable path on every pairwise
        // comparison across a shuffled set (a direct check, since NULL never being "Equal" to
        // another NULL rules out comparing two independently-sorted lists element-by-element).
        T[] shuffled = [small, nullValue, big, small];
        foreach (T x in shuffled)
            foreach (T y in shuffled)
                Assert.Equal(Math.Sign(x.CompareTo(y)), Math.Sign(((IComparable)x).CompareTo(y)));
    }

    private static bool GenericEquals<T>(T a, T b) where T : IEquatable<T> => a.Equals(b);
}
