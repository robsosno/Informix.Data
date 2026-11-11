using System.Data;
using System.Reflection;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// <see cref="IfxParameter"/>'s <see cref="ICloneable"/> implementation.
///
/// Spring.NET's ADO.NET layer copies the parameters of a declared command template
/// before each execution and reaches them through the *non-generic* <see cref="ICloneable"/>
/// — a provider whose parameter type does not implement it fails at run time (an
/// InvalidCastException deep inside the framework), never at compile time. Hence the
/// interface assertion below is a real contract test, not a tautology.
/// </summary>
public class IfxParameterCloneTests
{
    [Fact]
    public void Parameter_implements_the_non_generic_ICloneable()
    {
        Assert.IsAssignableFrom<ICloneable>(new IfxParameter());
    }

    [Fact]
    public void Clone_through_the_ICloneable_facade_returns_an_independent_IfxParameter()
    {
        var source = new IfxParameter("@p", 42) { IfxType = IfxType.Integer };

        object copy = ((ICloneable)source).Clone();

        var clone = Assert.IsType<IfxParameter>(copy);
        Assert.NotSame(source, clone);
        Assert.Equal("@p", clone.ParameterName);
        Assert.Equal(42, clone.Value);
        Assert.Equal(IfxType.Integer, clone.IfxType);
    }

    /// <summary>
    /// Every public read/write property the type actually stores has to survive Clone().
    /// Driven by reflection rather than a fixed list so that a property added to
    /// <see cref="IfxParameter"/> later fails here instead of being silently dropped from
    /// the copy — that omission is exactly the bug class this test guards.
    /// </summary>
    [Fact]
    public void Clone_copies_every_settable_property_the_type_honors()
    {
        var source = new IfxParameter();
        var fresh = new IfxParameter();
        var probed = new Dictionary<string, object?>();

        foreach (var p in typeof(IfxParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite)
                continue;
            object? probe = ProbeValue(p, fresh);
            if (probe is null)
                continue; // no probe defined for this property type — extend ProbeValue
            p.SetValue(source, probe);
            // DbParameter exposes some members as no-op virtuals (an empty base setter).
            // Nothing is stored, so there is nothing for Clone to copy either.
            if (!Equals(p.GetValue(source), probe))
                continue;
            probed[p.Name] = probe;
        }

        var clone = source.Clone();

        // Guard against the probe loop silently degenerating to "nothing to check".
        Assert.Contains("Value", probed.Keys);
        Assert.Contains("IfxType", probed.Keys);
        Assert.Contains("ParameterName", probed.Keys);

        foreach (var (name, expected) in probed)
        {
            object? actual = typeof(IfxParameter).GetProperty(name)!.GetValue(clone);
            Assert.True(
                Equals(expected, actual),
                $"Clone() dropped '{name}': expected {expected}, got {actual?.ToString() ?? "null"}.");
        }
    }

    /// <summary>The readable counterpart to the reflection sweep above: the whole
    /// documented ADO.NET surface, set to non-default values and asserted one by one.</summary>
    [Fact]
    public void Clone_copies_the_documented_ado_surface()
    {
        var source = new IfxParameter
        {
            ParameterName = "@amount",
            Value = 12.5m,
            IfxType = IfxType.Decimal,
            SmartLobColumn = "t.c",
            DbType = DbType.Decimal,
            Direction = ParameterDirection.InputOutput,
            IsNullable = true,
            SourceColumn = "amount_col",
            SourceColumnNullMapping = true,
            Size = 16,
            Precision = 10,
            Scale = 2,
        };

        var clone = source.Clone();

        Assert.Equal("@amount", clone.ParameterName);
        Assert.Equal(12.5m, clone.Value);
        Assert.Equal(IfxType.Decimal, clone.IfxType);
        Assert.Equal(DbType.Decimal, clone.DbType);
        Assert.Equal(ParameterDirection.InputOutput, clone.Direction);
        Assert.True(clone.IsNullable);
        Assert.Equal("amount_col", clone.SourceColumn);
        Assert.True(clone.SourceColumnNullMapping);
        Assert.Equal(16, clone.Size);
        Assert.Equal((byte)10, clone.Precision);
        Assert.Equal((byte)2, clone.Scale);
        Assert.Equal("t.c", clone.SmartLobColumn);
    }

    [Fact]
    public void Mutating_the_clone_does_not_touch_the_original()
    {
        var source = new IfxParameter("@p", "original") { IfxType = IfxType.VarChar, Size = 8 };

        var clone = source.Clone();
        clone.ParameterName = "@other";
        clone.Value = "changed";
        clone.IfxType = IfxType.Integer;
        clone.Size = 99;

        Assert.Equal("@p", source.ParameterName);
        Assert.Equal("original", source.Value);
        Assert.Equal(IfxType.VarChar, source.IfxType);
        Assert.Equal(8, source.Size);
    }

    [Fact]
    public void Cloning_a_default_parameter_yields_the_defaults()
    {
        var clone = new IfxParameter().Clone();
        var fresh = new IfxParameter();

        Assert.Equal(fresh.ParameterName, clone.ParameterName);
        Assert.Equal(fresh.Value, clone.Value);
        Assert.Equal(fresh.IfxType, clone.IfxType);
        Assert.Equal(fresh.DbType, clone.DbType);
        Assert.Equal(fresh.Direction, clone.Direction);
        Assert.Equal(fresh.IsNullable, clone.IsNullable);
        Assert.Equal(fresh.SourceColumn, clone.SourceColumn);
        Assert.Equal(fresh.Size, clone.Size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text")]
    public void Clone_preserves_null_and_DBNull_values(object? value)
    {
        // A null Value and a DBNull.Value bind differently, so the copy must not
        // conflate them (or normalize one into the other).
        Assert.Equal(value, new IfxParameter("@p", value).Clone().Value);
        Assert.Same(DBNull.Value, new IfxParameter("@p", DBNull.Value).Clone().Value);
    }

    [Fact]
    public void Cloned_parameters_can_be_added_to_a_fresh_command()
    {
        // The Spring.NET shape: a template parameter is cloned into each new command,
        // so the clone must be a fully functional, unowned IfxParameter.
        var template = new IfxParameter("@id", 7) { IfxType = IfxType.Integer };

        using var cn = new IfxConnection();
        using var cmd = cn.CreateCommand();
        cmd.Parameters.Add(template.Clone());
        cmd.Parameters.Add(template.Clone());

        Assert.Equal(2, cmd.Parameters.Count);
        Assert.Equal(7, ((IfxParameter)cmd.Parameters[0]!).Value);
        Assert.NotSame(cmd.Parameters[0], cmd.Parameters[1]);
    }

    /// <summary>A value guaranteed to differ from the property's default on a fresh
    /// instance, so "copied" can never be confused with "left at the default".</summary>
    private static object? ProbeValue(PropertyInfo p, IfxParameter fresh)
    {
        Type t = p.PropertyType;
        if (t == typeof(string)) return "probe-" + p.Name;
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return 7;
        if (t == typeof(byte)) return (byte)3;
        if (t == typeof(object)) return "probe-value";
        if (t.IsEnum)
        {
            object? current = p.GetValue(fresh);
            foreach (object v in Enum.GetValues(t))
                if (!v.Equals(current))
                    return v;
        }
        return null;
    }
}
