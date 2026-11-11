using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Informix.Data.Tests;

/// <summary>
/// Freezes the public API surface of Informix.Data against a checked-in
/// <c>PublicApi.approved.txt</c> (a poor-man's PublicApiAnalyzer): any added,
/// removed or changed public member fails this test, catching accidental leaks of
/// internals or unintended surface drift. To intentionally change the surface,
/// delete the approved file and re-run to regenerate it, then review the diff.
/// </summary>
public class ApiShapeGuardTests
{
    [Fact]
    public void Public_surface_matches_the_approved_snapshot()
    {
        string actual = DumpPublicApi(typeof(IfxConnection).Assembly);
        string approvedPath = ApprovedPath();

        if (!File.Exists(approvedPath))
        {
            File.WriteAllText(approvedPath, actual);
            return; // bootstrap: first run writes the snapshot.
        }

        string approved = File.ReadAllText(approvedPath).Replace("\r\n", "\n");
        if (approved != actual.Replace("\r\n", "\n"))
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(approvedPath)!, "PublicApi.received.txt"), actual);
            var firstDiff = FirstDifference(approved, actual.Replace("\r\n", "\n"));
            Assert.Fail($"Public API surface changed. First difference near:\n{firstDiff}\n" +
                        "Review PublicApi.received.txt; if intended, replace PublicApi.approved.txt with it.");
        }
    }

    /// <summary>
    /// The approved-snapshot test above catches any change to the public surface, but a
    /// missing interface on a *newly added* type wouldn't show up as a diff worth noticing
    /// at a glance -- this pins down the specific, easy-to-regress contract: these three
    /// types must implement the non-generic <see cref="ICloneable"/>.
    /// </summary>
    [Fact]
    public void Cloneable_types_implement_the_non_generic_ICloneable()
    {
        Assert.True(typeof(ICloneable).IsAssignableFrom(typeof(IfxParameter)));
        Assert.True(typeof(ICloneable).IsAssignableFrom(typeof(IfxCommand)));
        Assert.True(typeof(ICloneable).IsAssignableFrom(typeof(IfxConnection)));
        Assert.True(typeof(ICloneable).IsAssignableFrom(typeof(IfxDataAdapter)));
    }

    /// <summary>
    /// The four nullable value structs must genuinely implement <see cref="IEquatable{T}"/> and
    /// <see cref="IComparable{T}"/> for themselves (not just <see cref="IComparable"/>), so
    /// generic consumers (<c>List&lt;T&gt;.Sort()</c>, <c>EqualityComparer&lt;T&gt;.Default</c>)
    /// avoid boxing. See <c>ValueStructEquatableComparableTests</c> for the behavioral parity.
    /// </summary>
    [Fact]
    public void Value_structs_implement_the_generic_IEquatable_and_IComparable()
    {
        foreach (var t in new[] { typeof(IfxDecimal), typeof(IfxDateTime), typeof(IfxTimeSpan), typeof(IfxMonthSpan) })
        {
            Assert.True(typeof(IEquatable<>).MakeGenericType(t).IsAssignableFrom(t), $"{t.Name} should implement IEquatable<{t.Name}>");
            Assert.True(typeof(IComparable<>).MakeGenericType(t).IsAssignableFrom(t), $"{t.Name} should implement IComparable<{t.Name}>");
        }
    }

    private static string ApprovedPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "PublicApi.approved.txt");

    private static string DumpPublicApi(Assembly asm)
    {
        var sb = new StringBuilder();
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct"
                : t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : t.IsSealed ? "sealed class" : "class";
            string baseType = t.IsEnum || t.BaseType is null || t.BaseType.FullName == "System.Object" ? "" : $" : {t.BaseType.FullName}";
            var ifaces = t.GetInterfaces().Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal);
            string ifaceList = t.IsEnum ? "" : $" [{string.Join(",", ifaces)}]";
            sb.Append(kind).Append(' ').Append(t.FullName).Append(baseType).Append(ifaceList).Append('\n');

            if (t.IsEnum)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => Convert.ToInt64(f.GetRawConstantValue())))
                    sb.Append("  ").Append(f.Name).Append(" = ").Append(f.GetRawConstantValue()).Append('\n');
                continue;
            }

            const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var line in
                t.GetConstructors(Pub).Select(c => $".ctor({Params(c.GetParameters())})")
                .Concat(t.GetProperties(Pub).Select(p => $"prop {p.PropertyType.Name} {p.Name}{(p.CanRead ? " get" : "")}{(p.CanWrite ? " set" : "")}"))
                .Concat(t.GetMethods(Pub).Where(m => !m.IsSpecialName).Select(m => $"{m.ReturnType.Name} {m.Name}({Params(m.GetParameters())})"))
                .Concat(t.GetEvents(Pub).Select(e => $"event {e.EventHandlerType?.Name} {e.Name}"))
                .Concat(t.GetFields(Pub).Select(f => $"field {f.FieldType.Name} {f.Name}"))
                .OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.Append("  ").Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Params(ParameterInfo[] ps) => string.Join(", ", ps.Select(p => p.ParameterType.Name));

    private static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            string x = i < la.Length ? la[i] : "<end>";
            string y = i < lb.Length ? lb[i] : "<end>";
            if (x != y)
                return $"  line {i + 1}:\n    approved: {x}\n    actual:   {y}";
        }
        return "  (no line difference — trailing whitespace?)";
    }
}
