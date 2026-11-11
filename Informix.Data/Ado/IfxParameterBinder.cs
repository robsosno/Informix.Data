using System.Data;
using System.Text;
using Informix.Data.Sqli;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Encodes an <see cref="IfxParameterCollection"/> into SQ_BIND values, shared by
/// <see cref="IfxCommand"/> and <see cref="IfxBatchCommand"/> so the parameter-binding
/// rules (positional vs. named placeholders, smart-LOB upload, JSON/BSON framing) live
/// in exactly one place.
/// </summary>
internal static class IfxParameterBinder
{
    // IBM JDBC's -79887 ("Parameter name not found"): a named placeholder that resolves to no parameter.
    private const int UnmatchedParameterSqlCode = -79887;

    /// <summary>
    /// Encodes the parameters that back the statement's placeholders, in positional order,
    /// for SQ_BIND.
    ///
    /// <para>A <paramref name="parsedTextCommand"/> parse with named placeholders binds
    /// by name, in placeholder-occurrence order; otherwise (positional text, or a
    /// <see cref="CommandType.StoredProcedure"/> routine call) parameters bind in
    /// collection order.</para>
    ///
    /// <para>For <see cref="CommandType.StoredProcedure"/> the driver writes the
    /// <c>EXECUTE PROCEDURE</c> text itself from the input parameters alone — an output or
    /// return-value parameter conventionally receives a value the routine RETURNs and gets
    /// no <c>?</c> — so those parameters are skipped here to keep the bind count equal to
    /// the placeholder count. For <see cref="CommandType.Text"/> the caller wrote the
    /// placeholders, so every parameter binds: a <c>?</c> the caller typed always needs a
    /// value, whatever direction they gave the parameter behind it.</para>
    /// </summary>
    public static List<SqliBindValue> Build(
        SqliSession session, CommandType commandType, IfxParsedCommand? parsedTextCommand,
        IfxParameterCollection parameters)
    {
        if (commandType == CommandType.Text && parsedTextCommand is { IsNamed: true } parsed)
            return BuildNamedBinds(session, parsed, parameters);
        var binds = BuildPositionalBinds(
            session, parameters,
            skipOutputs: commandType == CommandType.StoredProcedure,
            skipFirst: parsedTextCommand is { HasReturnValuePlaceholder: true });
        // Positional text binds by position, so the counts have to agree. The server would reject a
        // mismatch with -254 after the round trip (and the bound values, smart LOBs included, would
        // already have been sent); a named statement, by contrast, ignores parameters nothing refers to.
        if (commandType == CommandType.Text && parsedTextCommand is { } text)
        {
            int supplied = binds.Count;
            foreach (IfxParameter p in parameters)
                if (p.Direction == ParameterDirection.ReturnValue)
                    supplied--;   // a routine's RETURN value has no '?' of its own
            if (supplied != text.PlaceholderCount)
                throw new InvalidOperationException(
                    $"The statement has {text.PlaceholderCount} positional '?' placeholder(s) but the Parameters collection " +
                    $"supplies {supplied}; positional parameters are matched by position, so the counts must agree.");
        }
        return binds;
    }

    // Positional binding (the native Informix convention and the CommandType.StoredProcedure
    // path): walk the collection in order, one bind per placeholder. See Build for which
    // parameters are skipped and why.
    private static List<SqliBindValue> BuildPositionalBinds(
        SqliSession session, IfxParameterCollection parameters, bool skipOutputs, bool skipFirst)
    {
        var binds = new List<SqliBindValue>(parameters.Count);
        bool first = true;
        foreach (IfxParameter p in parameters)
        {
            if (first)
            {
                first = false;
                // The leading '?' of an ODBC "{? = call ...}" escape is the return value.
                if (skipFirst)
                    continue;
            }
            if (skipOutputs && p.Direction is ParameterDirection.Output or ParameterDirection.ReturnValue)
                continue;
            binds.Add(EncodeBind(session, p));
        }
        return binds;
    }

    // Named binding: resolve each placeholder occurrence against the parameter collection
    // by name (@/:-tolerant, case-insensitive) and encode its value. Duplicate names bind
    // the same value once per occurrence; parameters that no placeholder references are
    // simply not bound (matching Npgsql).
    private static List<SqliBindValue> BuildNamedBinds(
        SqliSession session, IfxParsedCommand parsed, IfxParameterCollection parameters)
    {
        var binds = new List<SqliBindValue>(parsed.ParameterNames.Count);
        foreach (string name in parsed.ParameterNames)
        {
            IfxParameter p = FindParameterByName(parameters, name)
                ?? throw new IfxException(
                    $"The named parameter placeholder ':{name}'/'@{name}' has no matching parameter " +
                    $"in the command's Parameters collection ({UnmatchedParameterSqlCode}).",
                    UnmatchedParameterSqlCode);
            binds.Add(EncodeBind(session, p));
        }
        return binds;
    }

    // Matches a placeholder name against a parameter name case-insensitively, tolerating a
    // leading '@' or ':' on either side, so Add("@id"), ":id" and "id" all match ':id'/'@id'.
    private static IfxParameter? FindParameterByName(IfxParameterCollection parameters, string placeholderName)
    {
        foreach (IfxParameter p in parameters)
            if (string.Equals(StripSigil(p.ParameterName), placeholderName, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    // Matching ignores a leading '@' or ':' and the case.
    private static string StripSigil(string name) =>
        name.Length > 0 && (name[0] == '@' || name[0] == ':') ? name[1..] : name;

    // Encodes one parameter's value into a SQ_BIND value. A smart-large-object parameter
    // (IfxType.Clob/Blob) is uploaded first — ifx_lo_create + SQ_LODATA write + ifx_lo_close
    // on this session — and its 72-byte locator is what actually binds.
    internal static SqliBindValue EncodeBind(SqliSession session, IfxParameter p)
    {
        if (p.IfxType is IfxType.Clob or IfxType.Blob)
        {
            bool isClob = p.IfxType == IfxType.Clob;
            byte[]? payload = p.Value switch
            {
                null or DBNull => null,
                string s => Wire.IfxWireConverter.EncodeChar(s, session.DbEncoding),
                byte[] b => b,
                _ => throw new NotSupportedException(
                    $"A {p.IfxType} parameter must be a string or byte[], not {p.Value.GetType()}."),
            };
            byte[]? locator = payload is null ? null : session.CreateSmartBlob(payload, p.SmartLobColumn);
            return SqliBindValue.SmartBlobLocator(locator, isClob);
        }
        if (p.IfxType is IfxType.Json or IfxType.Bson)
            return EncodeJsonOrBsonBind(p);
        if (p.IfxType is IfxType.BinaryVar or IfxType.Binary18)
            return EncodeBinaryBind(p);
        return SqliBindValue.From(
            p.Value, session.DbEncoding, IfxTypeMap.ToWire(p.IfxType), session.Capabilities.IsLongRowId);
    }

    // BINARYVAR / BINARY18 (Database Extensions part 3) are opaque UDTs, bound exactly like JSON/BSON:
    // SENDRECV(44), owner "informix", the type name, data = [len:int32][raw bytes] (SqliBindValue.Udt).
    // The raw byte stream is what the server wants -- the "first byte is the length" of the manual is
    // internal to on-disk storage (live-probed, see informix-binary-data-types). A byte[] given to a
    // plain parameter binds as BYTE, which the server cannot cast to either type (-9634), hence the
    // explicit IfxType. BINARY18 is padded with zero bytes to its declared width here: a short
    // byte-stream bind leaves the server filling the tail with uninitialised memory. An over-long
    // BINARY18 is refused with -9636 (the server would silently truncate it); BINARYVAR's own
    // 253-byte ceiling is the server's to enforce (it answers -9636 too).
    private const int Binary18Width = 18;
    private const int OpaqueTypeTooLongSqlCode = -9636;

    private static SqliBindValue EncodeBinaryBind(IfxParameter p)
    {
        bool fixedWidth = p.IfxType == IfxType.Binary18;
        string typeName = fixedWidth ? "binary18" : "binaryvar";
        byte[]? payload = p.Value switch
        {
            null or DBNull => null,
            byte[] b => b,
            _ => throw new NotSupportedException(
                $"A {p.IfxType} parameter must be a byte[], not {p.Value.GetType()}."),
        };
        if (payload is null)
            return SqliBindValue.TypedNull(SqliTypeCode.SendRecv, typeName, "informix");
        if (fixedWidth)
        {
            if (payload.Length > Binary18Width)
                throw new IfxException(
                    IfxException.TextForEmptyMessage(OpaqueTypeTooLongSqlCode) + $" (binary18 holds {Binary18Width} bytes, got {payload.Length}).",
                    OpaqueTypeTooLongSqlCode);
            if (payload.Length < Binary18Width)
                payload = [.. payload, .. new byte[Binary18Width - payload.Length]];
        }
        if (payload.Length > short.MaxValue)
            throw new NotSupportedException(
                $"{p.IfxType} parameter payload of {payload.Length} bytes exceeds the driver's inline bind limit of {short.MaxValue} bytes.");
        return SqliBindValue.Udt(typeName, payload, precision: (short)payload.Length, owner: "informix");
    }

    // JSON/BSON are opaque built-ins that travel inline (no smart-LOB staging, unlike
    // Clob/Blob): type SENDRECV(44), extended owner "informix", name "json"/"bson", data
    // = [len:int32][payload] (SqliBindValue.Udt) — the same shape genbson()/BSON columns
    // use on the wire (see wire-protocol.md §8). A BSON parameter accepts a JSON string
    // (auto-encoded via BsonMediaTypeFormatter) or already-encoded raw BSON bytes; a JSON
    // parameter accepts only a string (bound as UTF-8 text, no BSON conversion).
    private static SqliBindValue EncodeJsonOrBsonBind(IfxParameter p)
    {
        bool isBson = p.IfxType == IfxType.Bson;
        string typeName = isBson ? "bson" : "json";
        byte[]? payload = p.Value switch
        {
            null or DBNull => null,
            byte[] b when isBson => b,
            string s when isBson => BsonMediaTypeFormatter.ToBson(s),
            string s => Encoding.UTF8.GetBytes(s),
            _ => throw new NotSupportedException(isBson
                ? $"A {p.IfxType} parameter must be a JSON string (auto-encoded to BSON) or a raw byte[] (already-encoded BSON), not {p.Value.GetType()}."
                : $"A {p.IfxType} parameter must be a string, not {p.Value.GetType()}."),
        };
        if (payload is { Length: > short.MaxValue })
            throw new NotSupportedException(
                $"{p.IfxType} parameter payload of {payload.Length} bytes exceeds the driver's inline bind limit of {short.MaxValue} bytes.");
        return payload is null
            ? new SqliBindValue { Type = SqliTypeCode.SendRecv, Data = null, ExtendedOwner = "informix", ExtendedTypeName = typeName }
            : SqliBindValue.Udt(typeName, payload, precision: (short)payload.Length, owner: "informix");
    }
}
