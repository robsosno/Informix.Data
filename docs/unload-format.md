# Values as text — UNLOAD files and `IfxTextConvert`

An Informix UNLOAD file (`.unl`) is one line per row, one field per column, each value written
the way the server writes it: a DATE in the `DBDATE` format, a DECIMAL with the locale's
separators, a DATETIME in the ANSI form its qualifier selects. `IfxTextConvert` does that
per-value conversion in both directions, so a caller who knows a table's columns can write the
row loop — the part that is easy once the values behave, and impossible to make generic without
knowing the schema.

```csharp
using Informix.Data;

// UNLOAD: one line per row, '|' after every field, as the server writes it.
using var reader = command.ExecuteReader();
while (reader.Read())
{
    var fields = new[]
    {
        reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
        reader.GetString(1),
        IfxTextConvert.FormatDate(DateOnly.FromDateTime(reader.GetDateTime(2))),
        IfxTextConvert.FormatDecimal(reader.GetDecimal(3), scale: 2),
    };
    writer.WriteLine(string.Join('|', fields) + "|");
}

// LOAD: the same rules, the other way round.
string[] fields = line.Split('|');
command.Parameters["@id"].Value    = IfxTextConvert.ParseInt32(fields[0]);
command.Parameters["@name"].Value  = fields[1];
command.Parameters["@due"].Value   = IfxTextConvert.ParseDate(fields[2]);
command.Parameters["@price"].Value = IfxTextConvert.ParseDecimal(fields[3]);
```

An empty field is SQL NULL in a `.unl` file, and that is what these methods do with it: a blank
string parses to `null` / `IfxDecimal.Null`, and a NULL formats back to an empty string.

The conversions are the client library's own — `deccvasc`, `rstrdate`, `dtcvasc` and the rest of
that family — down to the formats, the environment variables and the error codes, so a file this
code writes loads with `dbload`/`LOAD` and a file those produce reads back here. That also makes
the class the drop-in replacement when porting ESQL/C code that calls those functions directly.

## What maps to what

| Client library function          | Managed API |
| -------------------------------- | ----------- |
| `deccvasc`                       | `ParseDecimal`, `TryParseDecimal` |
| `dectoasc`                       | `FormatDecimal`, `TryFormatDecimal` |
| `rstoi`                          | `ParseInt32`, `TryParseInt32` |
| `rstol`                          | `ParseInt64`, `TryParseInt64` |
| `ifx_int8cvasc`, `bigintcvasc`   | `ParseBigInt`, `TryParseBigInt` |
| `ifx_int8toasc`                  | `TryFormatInt8` |
| `biginttoasc`                    | `FormatBigInt`, `TryFormatBigInt` |
| `rstrdate`                       | `ParseDate`, `TryParseDate` |
| `rdatestr`                       | `FormatDate` |
| `rmdyjul`, `rjulmdy`             | `ToInternalDate`, `FromInternalDate` |
| `dtcvasc`                        | `ParseDateTime`, `TryParseDateTime` |
| `dttoasc`                        | `FormatDateTime` |
| `incvasc`                        | `ParseDayTimeInterval`, `ParseYearMonthInterval` (+ `Try…`) |
| `intoasc`                        | `FormatInterval` |

Two shapes replace the C conventions:

* **Buffers.** The C functions fill a caller buffer, left-justified and blank-padded, and
  `dectoasc`/`ifx_int8toasc` never NUL-terminate it. The `Format…` methods return a trimmed
  string; the `TryFormat…(…, Span<char> destination, …)` overloads reproduce the buffer
  behaviour exactly, including `dectoasc`'s exponential fallback and its asterisk fill — which
  is what you want when writing a fixed-width file rather than a delimited one.
* **NULL.** Where a C function writes its null representation — a blank string into
  `deccvasc`, `-32768` into `rstoi`, an empty `dttoasc` buffer — the managed API uses the
  nullable value: `IfxDecimal.Null`, `null`, an empty string.

Errors keep the C codes: `Parse…`/`Format…` throw `IfxException` with `IfxSqlCode` set to
`-1213`, `-1205`, `-1262` and so on, and every one has a `Try…` overload that returns `false` —
handy when a bad line in a load file should be reported and skipped rather than thrown.

## Formats and environment

An `IfxTextFormat` carries the same settings Informix reads from the environment. **The process
environment is never consulted implicitly**: a call that is given no format uses
`IfxTextFormat.Default` (`MDY4/`, `.` and `,`), so a conversion does not change shape with the
machine it runs on. Where the settings come from is explicit:

* `connection.TextFormat` (or `IfxConnectionStringBuilder.TextFormat`) — built from the connection
  string's `DB Date`, `GL Date`, `DB Century`, `GL DateTime` and `Strict Thousands Separator` keys,
  i.e. the same `DBDATE` the server is told for that session, so client and server agree;
* `new IfxTextFormat { ... }` — fully explicit.

The settings, with the Informix variable each corresponds to:

| Variable | Used by | Effect |
| -------- | ------- | ------ |
| `DBDATE` | `ParseDate`, `FormatDate` | End-user DATE format: `MDY4/`, `DMY2.`, `Y4MD-`, `MDY40` (a `0` separator means none). Defaults to `MDY4/`. |
| `GL_DATE` | `ParseDate`, `FormatDate` | strftime-style DATE format, used only when `DBDATE` is unset. |
| `DBCENTURY` | `ParseDate`, `ParseDateTime` | Expands a one- or two-digit year: `R` present (default), `P` past, `F` future, `C` closest. |
| `GL_DATETIME` | `FormatDateTime` | strftime-style DATETIME format. `dtcvasc` ignores it, and so does `ParseDateTime`. |
| `IFX_NOT_STRICT_THOUS_SEP` | `ParseDecimal`, `ParseBigInt` | `1` accepts thousands separators outside three-digit groups. |

An explicit `IfxTextFormat` is also the way to make an unload file look the same on every
machine:

```csharp
var unl = new IfxTextFormat { DbDate = "Y4MD-" };   // 2008-12-31, whatever DBDATE says
```

The same type carries the decimal and thousands separators (which Informix takes from the client
locale; this provider has no GLS locale files, so they default to `.` and `,`) and
`ReferenceDate`, the "today" that `DBCENTURY`'s `P`/`F`/`C` rules measure against — which is also
how the unit tests keep two-digit years from drifting with the calendar.

### The machine's regional settings are not consulted

Nothing here reads the operating system's regional settings, `CultureInfo.CurrentCulture` or
`CLIENT_LOCALE`. With no Informix variable set the format is `MDY4/` with `.` and `,` whatever
the workstation says — the same thing the client library does.

It is also the only shape that keeps working. A `.unl` file is read back by `dbload`, `LOAD` or
another program, so it must not change with the machine that wrote it; and most system patterns
cannot be said in Informix's vocabulary anyway — `DBDATE` is three fields and a single separator
character, and groups are three digits, while Polish and French put a non-breaking space
(`U+00A0`, `U+202F`) in the thousands position, Hungarian short dates carry two different
separators, and Indian grouping is 3;2.

So say what the file should look like, rather than inheriting it:

```csharp
var polish = new IfxTextFormat { DbDate = "DMY4.", DecimalSeparator = ',' };
```

For text meant for a screen or a report rather than for a file, use .NET's own formatting, which
does follow the operating system:

```csharp
string shown = due.ToString("d");        // 31.12.2008 on a Polish machine
```

The strftime subset understood in `GL_DATE`/`GL_DATETIME` is
`%d %e %m %y %Y %b %h %B %a %A %j %H %I %M %S %p %T %R %F %n %t %%`; anything else is copied
through. As in the C library, `%Y` is a full year, so `DBCENTURY` never applies to it.

## Fidelity

The formats, environment handling and error codes were read out of the shipping client library
(`isqlt15a.dll`, Client SDK 4.50) and then checked against it. `tools/TextConvertProbe` calls
both implementations with the same inputs and reports every difference: a fixed suite plus a
randomized pass of roughly 34,000 comparisons across numbers, dates, DATETIMEs and INTERVALs,
under several `DBDATE`, `DBCENTURY` and `GL_DATE` settings. The expectations it confirmed are
frozen as ordinary unit tests in `Informix.Data.Tests/IfxTextConvertTests.cs`, which need no SDK.

Behaviour that merely looks odd is replicated rather than corrected, because a file has to load
into the same values the server would have written — the field widths `ParseDate` accepts are
the example, down to which error code a malformed date gets.

One difference is deliberate:

> `bigintcvasc` drops a negative exponent whose shift is wider than the digit string instead of
> applying it, so it reads `"1.5e-13"` as **15** — while the same value written
> `"0.00000000000015"`, and `"1.5e-1"` too, give 0. That is a plain bug rather than a
> convention a file format could depend on, so `ParseBigInt` applies the exponent whatever its
> size and answers 0. `tools/TextConvertProbe` knows about this case and reports it separately
> from real mismatches.

Two more are structural rather than choices:

* A DECIMAL holds 32 significant digits and exponents to ±126, more than a CLR `decimal`.
  `ParseDecimal` therefore throws `OverflowException` outside the `decimal` range; the
  text-to-text `FormatDecimal(ReadOnlySpan<char>, …)` overload keeps the full range, since it
  never converts to `decimal` at all — use it to rewrite a field's scale in place.
* The C `dec_t` has a signed zero, so `intoasc` writes `-0` for a negative zero interval.
  `IfxTimeSpan` and `IfxMonthSpan` have one zero, and `FormatInterval` writes it unsigned.
