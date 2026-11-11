using System.Data;
using System.Linq;
using Informix.Data.Types;

namespace Informix.Data;

/// <summary>
/// Builds the ADO.NET schema collections returned by
/// <see cref="IfxConnection.GetSchema()"/>. Standard collections
/// (MetaDataCollections, DataSourceInformation, DataTypes, Restrictions,
/// ReservedWords) are produced offline; the Informix collections (Tables, Views,
/// Columns, Indexes, Procedures, ProcedureParameters) are catalog queries over
/// <c>systables</c>/<c>syscolumns</c>/<c>sysindices</c>/<c>sysprocedures</c>.
/// </summary>
internal static class IfxSchema
{
    public const string MetaDataCollections = "MetaDataCollections";
    public const string DataSourceInformation = "DataSourceInformation";
    public const string DataTypes = "DataTypes";
    public const string Restrictions = "Restrictions";
    public const string ReservedWords = "ReservedWords";
    public const string Tables = "Tables";
    public const string Views = "Views";
    public const string Columns = "Columns";
    public const string Indexes = "Indexes";
    public const string Procedures = "Procedures";
    public const string ProcedureParameters = "ProcedureParameters";
    public const string ProcedureColumns = "ProcedureColumns";
    public const string PrimaryKeys = "PrimaryKeys";
    public const string ForeignKeys = "ForeignKeys";

    // (collection name, restriction count, identifier parts) for MetaDataCollections.
    private static readonly (string Name, int Restrictions, int Parts)[] Collections =
    [
        (MetaDataCollections, 0, 0),
        (DataSourceInformation, 0, 0),
        (DataTypes, 0, 0),
        (Restrictions, 0, 0),
        (ReservedWords, 0, 0),
        (Tables, 4, 3),
        (Views, 3, 3),
        (Columns, 4, 4),
        (Indexes, 4, 3),
        (Procedures, 3, 3),
        (ProcedureParameters, 4, 4),
        (ProcedureColumns, 4, 4),
        (PrimaryKeys, 3, 4),
        (ForeignKeys, 3, 4),
    ];

    // Collection names are matched case-insensitively. Restriction values are positional: catalog, owner, object, sub-object.
    public static DataTable Get(IfxConnection connection, string collectionName, string?[]? restrictions)
    {
        ArgumentException.ThrowIfNullOrEmpty(collectionName);
        return collectionName.ToLowerInvariant() switch
        {
            "metadatacollections" => BuildMetaDataCollections(),
            "datasourceinformation" => BuildDataSourceInformation(connection),
            "datatypes" => BuildDataTypes(),
            "restrictions" => BuildRestrictions(),
            "reservedwords" => BuildReservedWords(),
            "tables" => BuildTables(connection, restrictions),
            "views" => BuildViews(connection, restrictions),
            "columns" => BuildColumns(connection, restrictions),
            "indexes" => BuildIndexes(connection, restrictions),
            "procedures" => BuildProcedures(connection, restrictions),
            "procedureparameters" => BuildProcedureParameters(connection, restrictions),
            "procedurecolumns" => BuildProcedureColumns(connection, restrictions),
            "primarykeys" => BuildPrimaryKeys(connection, restrictions),
            "foreignkeys" => BuildForeignKeys(connection, restrictions),
            _ => throw new ArgumentException($"Unsupported schema collection '{collectionName}'.", nameof(collectionName)),
        };
    }

    private static DataTable BuildMetaDataCollections()
    {
        var t = new DataTable(MetaDataCollections);
        t.Columns.Add("CollectionName", typeof(string));
        t.Columns.Add("NumberOfRestrictions", typeof(int));
        t.Columns.Add("NumberOfIdentifierParts", typeof(int));
        foreach (var (name, r, p) in Collections)
            t.Rows.Add(name, r, p);
        return t;
    }

    private static DataTable BuildDataSourceInformation(IfxConnection connection)
    {
        var t = new DataTable(DataSourceInformation);
        t.Columns.Add("DataSourceProductName", typeof(string));
        t.Columns.Add("DataSourceProductVersion", typeof(string));
        t.Columns.Add("ParameterMarkerFormat", typeof(string));
        t.Columns.Add("ParameterMarkerPattern", typeof(string));
        t.Columns.Add("CompositeIdentifierSeparatorPattern", typeof(string));
        t.Columns.Add("IdentifierPattern", typeof(string));
        t.Columns.Add("QuotedIdentifierPattern", typeof(string));
        t.Columns.Add("StatementSeparatorPattern", typeof(string));
        t.Columns.Add("SupportedJoinOperators", typeof(int));
        var row = t.NewRow();
        row["DataSourceProductName"] = "Informix";
        row["DataSourceProductVersion"] = connection.State == ConnectionState.Open ? connection.ServerVersion : "";
        row["ParameterMarkerFormat"] = "?";
        row["ParameterMarkerPattern"] = @"(\?)";
        row["CompositeIdentifierSeparatorPattern"] = @"\.";
        row["IdentifierPattern"] = @"(^\[\p{Lo}\p{Lu}\p{Ll}_@#][\p{Lo}\p{Lu}\p{Ll}\p{Nd}@$#_]*$)";
        row["QuotedIdentifierPattern"] = "(([^\\\"]|\\\"\\\")*)";
        row["StatementSeparatorPattern"] = ";";
        // Bit mask of the supported join kinds (inner, left, right, full).
        row["SupportedJoinOperators"] = 15;
        t.Rows.Add(row);
        return t;
    }

    private static DataTable BuildDataTypes()
    {
        var t = new DataTable(DataTypes);
        t.Columns.Add("TypeName", typeof(string));
        t.Columns.Add("ProviderDbType", typeof(int));
        t.Columns.Add("DataType", typeof(string));
        t.Columns.Add("IsAutoIncrementable", typeof(bool));
        t.Columns.Add("IsFixedLength", typeof(bool));
        t.Columns.Add("IsLong", typeof(bool));
        t.Columns.Add("IsNullable", typeof(bool));

        void Add(string name, IfxType type, Type clr, bool autoInc = false, bool fixedLen = true, bool isLong = false) =>
            t.Rows.Add(name, (int)type, clr.FullName, autoInc, fixedLen, isLong, true);

        Add("CHAR", IfxType.Char, typeof(string), fixedLen: true);
        Add("VARCHAR", IfxType.VarChar, typeof(string), fixedLen: false);
        Add("LVARCHAR", IfxType.LVarChar, typeof(string), fixedLen: false, isLong: true);
        Add("NCHAR", IfxType.NChar, typeof(string));
        Add("NVARCHAR", IfxType.NVarChar, typeof(string), fixedLen: false);
        Add("SMALLINT", IfxType.SmallInt, typeof(short));
        Add("INTEGER", IfxType.Integer, typeof(int));
        Add("INT8", IfxType.Int8, typeof(long));
        Add("BIGINT", IfxType.BigInt, typeof(long));
        Add("SERIAL", IfxType.Serial, typeof(int), autoInc: true);
        Add("SERIAL8", IfxType.Serial8, typeof(long), autoInc: true);
        Add("BIGSERIAL", IfxType.BigSerial, typeof(long), autoInc: true);
        Add("SMALLFLOAT", IfxType.SmallFloat, typeof(float));
        Add("FLOAT", IfxType.Float, typeof(double));
        Add("DECIMAL", IfxType.Decimal, typeof(decimal));
        Add("MONEY", IfxType.Money, typeof(decimal));
        Add("DATE", IfxType.Date, typeof(DateOnly));
        Add("DATETIME", IfxType.DateTime, typeof(IfxDateTime));
        Add("INTERVAL DAY TO FRACTION", IfxType.IntervalDayFraction, typeof(TimeSpan));
        Add("INTERVAL YEAR TO MONTH", IfxType.IntervalYearMonth, typeof(IfxMonthSpan));
        Add("BOOLEAN", IfxType.Boolean, typeof(bool));
        Add("BYTE", IfxType.Byte, typeof(byte[]), isLong: true);
        Add("TEXT", IfxType.Text, typeof(string), isLong: true);
        Add("BLOB", IfxType.Blob, typeof(byte[]), isLong: true);
        Add("CLOB", IfxType.Clob, typeof(string), isLong: true);
        Add("JSON", IfxType.Json, typeof(string), fixedLen: false, isLong: true);
        Add("BSON", IfxType.Bson, typeof(string), fixedLen: false, isLong: true);
        return t;
    }

    private static DataTable BuildRestrictions()
    {
        var t = new DataTable(Restrictions);
        t.Columns.Add("CollectionName", typeof(string));
        t.Columns.Add("RestrictionName", typeof(string));
        t.Columns.Add("RestrictionNumber", typeof(int));
        void Add(string c, string r, int n) => t.Rows.Add(c, r, n);
        Add(Tables, "Catalog", 1); Add(Tables, "Owner", 2); Add(Tables, "Table", 3); Add(Tables, "TableType", 4);
        Add(Views, "Catalog", 1); Add(Views, "Owner", 2); Add(Views, "View", 3);
        Add(Columns, "Catalog", 1); Add(Columns, "Owner", 2); Add(Columns, "Table", 3); Add(Columns, "Column", 4);
        Add(Indexes, "Catalog", 1); Add(Indexes, "Owner", 2); Add(Indexes, "Table", 3); Add(Indexes, "Index", 4);
        Add(Procedures, "Catalog", 1); Add(Procedures, "Owner", 2); Add(Procedures, "Procedure", 3);
        Add(ProcedureParameters, "Catalog", 1); Add(ProcedureParameters, "Owner", 2);
        Add(ProcedureParameters, "Procedure", 3); Add(ProcedureParameters, "Parameter", 4);
        Add(ProcedureColumns, "Catalog", 1); Add(ProcedureColumns, "Owner", 2);
        Add(ProcedureColumns, "Procedure", 3); Add(ProcedureColumns, "Column", 4);
        Add(PrimaryKeys, "Catalog", 1); Add(PrimaryKeys, "Owner", 2); Add(PrimaryKeys, "Table", 3);
        Add(ForeignKeys, "Catalog", 1); Add(ForeignKeys, "Owner", 2); Add(ForeignKeys, "Table", 3);
        return t;
    }

    private static DataTable BuildReservedWords()
    {
        var t = new DataTable(ReservedWords);
        t.Columns.Add("ReservedWord", typeof(string));
        foreach (var w in ReservedWordList)
            t.Rows.Add(w);
        return t;
    }

    // A conservative core list of SQL words; not the complete Informix keyword set.
    private static readonly string[] ReservedWordList =
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "BEGIN", "BETWEEN", "BY", "CALL", "CASE",
        "CHAR", "CHECK", "CLOSE", "COMMIT", "CONNECT", "CREATE", "CURRENT", "CURSOR", "DATABASE",
        "DATE", "DATETIME", "DECIMAL", "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP",
        "ELSE", "END", "EXECUTE", "EXISTS", "FETCH", "FLOAT", "FOR", "FOREIGN", "FROM", "GRANT",
        "GROUP", "HAVING", "IN", "INDEX", "INSERT", "INTEGER", "INTO", "IS", "LIKE", "MONEY", "NOT",
        "NULL", "OF", "ON", "OPEN", "OR", "ORDER", "PRIMARY", "PROCEDURE", "REFERENCES", "REVOKE",
        "ROLLBACK", "SELECT", "SERIAL", "SET", "SMALLINT", "TABLE", "THEN", "TO", "UNION", "UNIQUE",
        "UPDATE", "USER", "USING", "VALUES", "VARCHAR", "VIEW", "WHERE", "WITH",
    ];

    // ---- catalog collections ----

    // Restriction value at the given position, or null if it was not supplied.
    private static string? R(string?[]? r, int i) => r is not null && r.Length > i ? r[i] : null;

    private static DataTable BuildTables(IfxConnection connection, string?[]? r) =>
        CatalogTables(connection, table: R(r, 2), type: R(r, 3), Tables, includeViews: true);

    private static DataTable BuildViews(IfxConnection connection, string?[]? r) =>
        CatalogTables(connection, table: R(r, 2), type: "VIEW", Views, includeViews: true);

    // tabtype 'V' is a view; every other kind (table, synonym, ...) is reported as TABLE.
    private static DataTable CatalogTables(IfxConnection connection, string? table, string? type, string name, bool includeViews)
    {
        var t = new DataTable(name);
        t.Columns.Add("TABLE_CATALOG", typeof(string));
        t.Columns.Add("TABLE_SCHEMA", typeof(string));
        t.Columns.Add("TABLE_NAME", typeof(string));
        t.Columns.Add("TABLE_TYPE", typeof(string));

        string sql = "SELECT owner, tabname, tabtype FROM systables WHERE tabid > 99";
        if (table is not null) sql += " AND tabname = ?";
        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (table is not null) cmd.Parameters.Add(new IfxParameter("t", table));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string tabType = reader.GetString(2).Trim() == "V" ? "VIEW" : "TABLE";
            if (type is not null && !string.Equals(tabType, type, StringComparison.OrdinalIgnoreCase))
                continue;
            t.Rows.Add(connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(), tabType);
        }
        return t;
    }

    // COLUMN_TYPE and COLUMN_LENGTH are the raw syscolumns.coltype / collength words; the decoded values (DATA_TYPE, TYPE_NAME,
    // COLUMN_SIZE, DECIMAL_DIGITS, NULLABLE) follow ODBC's SQLColumns. extended_id is needed to tell BLOB, CLOB, BOOLEAN, LVARCHAR and
    // JSON/BSON apart because they all share coltype 40/41.
    private static DataTable BuildColumns(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(Columns);
        t.Columns.Add("TABLE_CATALOG", typeof(string));
        t.Columns.Add("TABLE_SCHEMA", typeof(string));
        t.Columns.Add("TABLE_NAME", typeof(string));
        t.Columns.Add("COLUMN_NAME", typeof(string));
        t.Columns.Add("ORDINAL_POSITION", typeof(int));
        // COLUMN_TYPE / COLUMN_LENGTH are the raw syscolumns.coltype / collength words (flag
        // bits and packed lengths included) and stay as they always were; the five columns
        // after them are the decoded, ODBC SQLColumns-style view of the same two words.
        t.Columns.Add("COLUMN_TYPE", typeof(int));
        t.Columns.Add("COLUMN_LENGTH", typeof(int));
        t.Columns.Add("DATA_TYPE", typeof(int));
        t.Columns.Add("TYPE_NAME", typeof(string));
        t.Columns.Add("COLUMN_SIZE", typeof(int));
        t.Columns.Add("DECIMAL_DIGITS", typeof(int));
        t.Columns.Add("NULLABLE", typeof(bool));

        // extended_id is what tells BLOB (10) / CLOB (11) / BOOLEAN (5) / LVARCHAR (1) /
        // JSON (25) / BSON (26) apart: they all share coltype 40 or 41.
        string sql =
            "SELECT t.owner, t.tabname, c.colname, c.colno, c.coltype, c.collength, c.extended_id " +
            "FROM systables t, syscolumns c WHERE t.tabid = c.tabid AND t.tabid > 99";
        string? table = R(r, 2), column = R(r, 3);
        if (table is not null) sql += " AND t.tabname = ?";
        if (column is not null) sql += " AND c.colname = ?";
        sql += " ORDER BY t.tabname, c.colno";

        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (table is not null) cmd.Parameters.Add(new IfxParameter("t", table));
        if (column is not null) cmd.Parameters.Add(new IfxParameter("c", column));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int colType = reader.GetInt32(4), colLength = reader.GetInt32(5), xid = reader.GetInt32(6);
            var info = DescribeColumnType(colType, xid, colLength);
            t.Rows.Add(connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                reader.GetString(2).Trim(), reader.GetInt32(3), colType, colLength,
                (int)info.DataType, info.TypeName,
                info.Size is { } size ? size : DBNull.Value,
                info.Digits is { } digits ? digits : DBNull.Value,
                info.Nullable);
        }
        return t;
    }

    private static DataTable BuildIndexes(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(Indexes);
        t.Columns.Add("TABLE_CATALOG", typeof(string));
        t.Columns.Add("TABLE_SCHEMA", typeof(string));
        t.Columns.Add("TABLE_NAME", typeof(string));
        t.Columns.Add("INDEX_NAME", typeof(string));
        t.Columns.Add("UNIQUE", typeof(bool));

        string sql =
            "SELECT t.owner, t.tabname, i.idxname, i.idxtype " +
            "FROM systables t, sysindices i WHERE t.tabid = i.tabid AND t.tabid > 99";
        string? table = R(r, 2);
        if (table is not null) sql += " AND t.tabname = ?";
        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (table is not null) cmd.Parameters.Add(new IfxParameter("t", table));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            t.Rows.Add(connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                reader.GetString(2).Trim(), reader.GetString(3).Trim() == "U");
        return t;
    }

    private static DataTable BuildProcedures(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(Procedures);
        t.Columns.Add("PROCEDURE_CATALOG", typeof(string));
        t.Columns.Add("PROCEDURE_SCHEMA", typeof(string));
        t.Columns.Add("PROCEDURE_NAME", typeof(string));
        t.Columns.Add("PROCEDURE_TYPE", typeof(string));

        string sql = "SELECT owner, procname, isproc FROM sysprocedures";
        string? proc = R(r, 2);
        if (proc is not null) sql += " WHERE procname = ?";
        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (proc is not null) cmd.Parameters.Add(new IfxParameter("p", proc));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            t.Rows.Add(connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                reader.GetString(2).Trim() == "t" ? "PROCEDURE" : "FUNCTION");
        return t;
    }

    private static DataTable BuildProcedureParameters(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(ProcedureParameters);
        t.Columns.Add("PROCEDURE_CATALOG", typeof(string));
        t.Columns.Add("PROCEDURE_SCHEMA", typeof(string));
        t.Columns.Add("PROCEDURE_NAME", typeof(string));
        t.Columns.Add("PARAMETER_NAME", typeof(string));
        t.Columns.Add("ORDINAL_POSITION", typeof(int));

        string sql =
            "SELECT p.owner, p.procname, c.paramname, c.paramid " +
            "FROM sysprocedures p, sysproccolumns c WHERE p.procid = c.procid";
        string? proc = R(r, 2);
        if (proc is not null) sql += " AND p.procname = ?";
        sql += " ORDER BY p.procname, c.paramid";
        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (proc is not null) cmd.Parameters.Add(new IfxParameter("p", proc));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            t.Rows.Add(connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(), reader.GetInt32(3));
        return t;
    }

    // SQLProcedureColumns is one row per parameter *and* per column of the routine's
    // returned row, distinguished by COLUMN_TYPE. sysproccolumns carries both kinds of row,
    // and its paramattr column already speaks ODBC's numbering — live-verified against IDS
    // (14.10 engine): 1 = IN (SQL_PARAM_INPUT), 2 = INOUT (SQL_PARAM_INPUT_OUTPUT),
    // 3 = a RETURNING value (SQL_RESULT_COL; paramname is NULL and, in paramid order, these
    // rows precede the parameters), 4 = OUT (SQL_PARAM_OUTPUT), 5 = return value
    // (SQL_RETURN_VALUE), 0 = unknown — so paramattr passes through as COLUMN_TYPE verbatim.
    // The server-side ifx_param_types/ifx_ret_types helpers calls are
    // deliberately not used: it only ever feeds them into display text (TYPE_NAME/REMARKS),
    // they carry less detail than paramtype/paramxid/paramlen ("varchar" without the length,
    // "decimal" without precision/scale), and consuming them means comma-splitting free text.
    // The one thing they render better — DATETIME/INTERVAL qualifiers — IfxQualifier decodes
    // from paramlen directly ("datetime year to second", identical to ifx_ret_types output).
    private static DataTable BuildProcedureColumns(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(ProcedureColumns);
        t.Columns.Add("PROCEDURE_CATALOG", typeof(string));
        t.Columns.Add("PROCEDURE_SCHEMA", typeof(string));
        t.Columns.Add("PROCEDURE_NAME", typeof(string));
        t.Columns.Add("PARAMETER_NAME", typeof(string));
        t.Columns.Add("ORDINAL_POSITION", typeof(int));
        t.Columns.Add("COLUMN_TYPE", typeof(int));
        t.Columns.Add("DATA_TYPE", typeof(int));
        t.Columns.Add("TYPE_NAME", typeof(string));
        t.Columns.Add("COLUMN_SIZE", typeof(int));
        t.Columns.Add("DECIMAL_DIGITS", typeof(int));
        t.Columns.Add("NULLABLE", typeof(bool));

        string sql =
            "SELECT p.owner, p.procname, c.paramname, c.paramid, c.paramattr, c.paramtype, c.paramxid, c.paramlen " +
            "FROM sysprocedures p, sysproccolumns c WHERE p.procid = c.procid";
        string? proc = R(r, 2);
        if (proc is not null) sql += " AND p.procname = ?";
        sql += " ORDER BY p.procname, c.paramid";
        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (proc is not null) cmd.Parameters.Add(new IfxParameter("p", proc));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int paramAttr = reader.GetInt32(4);
            int paramType = reader.GetInt32(5);
            int paramXid = reader.GetInt32(6);
            int paramLen = reader.GetInt32(7);
            var (dataType, typeName) = ResolveProcColumnType(paramType, paramXid, paramLen);
            bool isNumeric = typeName is nameof(SqliTypeCode.Decimal) or nameof(SqliTypeCode.Money);
            t.Rows.Add(
                connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(), reader.GetInt32(3),
                paramAttr, (int)dataType, typeName,
                isNumeric ? paramLen >> 8 & 0xFF : paramLen,
                isNumeric ? paramLen & 0xFF : 0,
                (paramType & (int)IfxTypeFlags.NotNullable) == 0);
        }
        return t;
    }

    // Maps a catalog paramtype/paramxid pair — sysproccolumns uses the same low-byte-base/
    // high-byte-flags encoding as the SQLI wire descriptor — to the public IfxType and a
    // display name, collapsing the same opaque built-ins SqliSession.ReceiveDescribe
    // collapses on the wire (LVARCHAR/CLOB/BLOB/JSON/BSON/BOOLEAN) so a caller sees the
    // declared SQL type instead of the raw UDTVAR/UDTFIXED carrier. For DATETIME/INTERVAL,
    // paramlen is the packed qualifier word and TYPE_NAME spells it out
    // ("datetime year to second") — the same text ifx_ret_types renders server-side.
    private static (IfxType DataType, string TypeName) ResolveProcColumnType(int paramType, int paramXid, int paramLen)
    {
        var (_, dataType, typeName) = ResolveColumnTypeCore(paramType, paramXid, paramLen);
        return (dataType, typeName);
    }

    // Shared by Columns and ProcedureColumns. Opaque types (coltype 40/41) are told apart by their extended id; DATETIME/INTERVAL names
    // are built from the qualifier ("datetime year to second"), and an INTERVAL starting at YEAR or MONTH is a year-month interval.
    private static (SqliTypeCode Effective, IfxType DataType, string TypeName) ResolveColumnTypeCore(
        int paramType, int paramXid, int paramLen)
    {
        var baseType = (SqliTypeCode)(short)(paramType & 0xFF);
        bool isOpaque = baseType is SqliTypeCode.UdtVar or SqliTypeCode.UdtFixed;
        var effective = isOpaque
            ? paramXid switch
            {
                (int)IfxExtendedType.LVarChar or (int)IfxExtendedType.Llvc => SqliTypeCode.LVarChar,
                (int)IfxExtendedType.Clob => SqliTypeCode.Clob,
                (int)IfxExtendedType.Blob => SqliTypeCode.Blob,
                (int)IfxExtendedType.Json => SqliTypeCode.Json,
                (int)IfxExtendedType.Bson => SqliTypeCode.Bson,
                (int)IfxExtendedType.Boolean => SqliTypeCode.Bool,
                _ => baseType,
            }
            : baseType;
        string typeName = IfxQualifier.GetIfxTypeName(effective, (short)paramLen) ?? effective.ToString();
        // IfxTypeMap maps every INTERVAL to the day-fraction class; the qualifier's start field
        // (YEAR/MONTH = 0/2 vs DAY..FRACTION) says which of the two classes a column really is.
        var dataType = effective == SqliTypeCode.Interval && IfxQualifier.GetStartCode((short)paramLen) <= 2
            ? IfxType.IntervalYearMonth
            : IfxTypeMap.ToPublic(effective);
        return (effective, dataType, typeName);
    }

    /// <summary>The decoded form of a <c>syscolumns</c> <c>coltype</c>/<c>collength</c>/<c>extended_id</c> triple.</summary>
    /// <param name="Size">Declared length / precision, or <c>null</c> where none applies (smart LOBs: collength is the 72-byte locator, not a size).</param>
    /// <param name="Digits">Scale (DECIMAL/MONEY) or fractional-second digits (DATETIME/INTERVAL); <c>null</c> for a floating DECIMAL(p) (scale 255).</param>
    internal readonly record struct ColumnTypeInfo(IfxType DataType, string TypeName, int? Size, int? Digits, bool Nullable);

    /// <summary>
    /// Decodes a <c>syscolumns</c> row's type words. <c>coltype</c> is the low-byte base type plus flag
    /// bits (0x100 = NOT NULL); <c>collength</c> is packed per type — <c>(precision&lt;&lt;8)|scale</c> for
    /// DECIMAL/MONEY (scale 255 = floating), <c>(min&lt;&lt;8)|max</c> for VARCHAR/NVARCHAR, the
    /// DATETIME/INTERVAL qualifier word for those, a plain length otherwise. Types that share
    /// coltype 40/41 (BLOB, CLOB, BOOLEAN, LVARCHAR, JSON, BSON) are told apart by <c>extended_id</c>.
    /// </summary>
    internal static ColumnTypeInfo DescribeColumnType(int colType, int extendedId, int colLength)
    {
        var (effective, dataType, typeName) = ResolveColumnTypeCore(colType, extendedId, colLength);
        bool nullable = (colType & (int)IfxTypeFlags.NotNullable) == 0;
        switch (effective)
        {
            case SqliTypeCode.Decimal or SqliTypeCode.Money:
            {
                int scale = colLength & 0xFF;
                return new ColumnTypeInfo(dataType, typeName, colLength >> 8 & 0xFF, scale == 0xFF ? null : scale, nullable);
            }
            case SqliTypeCode.VarChar or SqliTypeCode.NVarChar:
                return new ColumnTypeInfo(dataType, typeName, colLength & 0xFF, 0, nullable);
            case SqliTypeCode.DateTime or SqliTypeCode.Interval:
                return new ColumnTypeInfo(dataType, typeName, IfxQualifier.GetLength((short)colLength),
                    IfxQualifier.GetScale((short)colLength), nullable);
            case SqliTypeCode.Clob or SqliTypeCode.Blob:
                return new ColumnTypeInfo(dataType, typeName, null, 0, nullable);
            default:
                return new ColumnTypeInfo(dataType, typeName, colLength, 0, nullable);
        }
    }

    // ---- primary/foreign keys (port of IfxDatabaseMetaData.getPrimaryKeys/getImportedKeys) ----

    // A server DECSIZE-wide composite key spans at most 16 columns (sysindexes.partN,
    // N=1..16); a negative partN means the column sorts descending, hence ABS(). Port of
    // the OR-of-matches getPrimaryKeys/getImportedKeys use to tie a column to its position
    // in a (possibly composite) key.
    private static string PartsOrClause(string colExpr, string idxAlias) =>
        string.Join(" OR ", Enumerable.Range(1, 16).Select(n => $"{colExpr} = ABS({idxAlias}.part{n})"));

    // As above, but pairing a referencing (FK) and referenced (PK) column against their
    // respective indexes at the same part position — port of getImportedKeys' "pc.colno=
    // ABS(pi.partN) and fc.colno=ABS(fi.partN)" OR-of-ANDs that aligns composite foreign-key
    // columns with the primary-key columns they reference.
    private static string PairedPartsOrClause(string pCol, string pIdxAlias, string fCol, string fIdxAlias) =>
        string.Join(" OR ", Enumerable.Range(1, 16)
            .Select(n => $"({pCol} = ABS({pIdxAlias}.part{n}) AND {fCol} = ABS({fIdxAlias}.part{n}))"));

    // The 1-based composite-key column position as a server-side CASE, so a query can ORDER
    // BY it directly. getPrimaryKeys instead finds this client-side in a loop over 16 columns
    // per row; getImportedKeys already computes it this way — this
    // port uses the server-side CASE for both, since GetSchema builds an in-memory DataTable
    // and can just add the column.
    private static string KeySeqCase(string colExpr, string idxAlias) =>
        "CASE " + string.Join(" ", Enumerable.Range(1, 16)
            .Select(n => $"WHEN {colExpr} = ABS({idxAlias}.part{n}) THEN {n}")) + " END";

    // Primary key columns through the constraint's backing index, ordered by their position in that index.
    private static DataTable BuildPrimaryKeys(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(PrimaryKeys);
        t.Columns.Add("TABLE_CATALOG", typeof(string));
        t.Columns.Add("TABLE_OWNER", typeof(string));
        t.Columns.Add("TABLE_NAME", typeof(string));
        t.Columns.Add("COLUMN_NAME", typeof(string));
        t.Columns.Add("KEY_SEQ", typeof(int));
        t.Columns.Add("PK_NAME", typeof(string));

        string? owner = R(r, 1), table = R(r, 2);
        string sql =
            "SELECT st.owner, st.tabname, sc.colname, so.constrname, " +
            KeySeqCase("sc.colno", "si") + " AS key_seq " +
            "FROM systables st, sysconstraints so, sysindexes si, syscolumns sc " +
            "WHERE st.tabid = so.tabid AND so.constrtype = 'P' AND so.idxname = si.idxname " +
            "AND sc.tabid = st.tabid AND st.tabid > 99 AND (" + PartsOrClause("sc.colno", "si") + ")";
        if (owner is not null) sql += " AND st.owner = ?";
        if (table is not null) sql += " AND st.tabname = ?";
        sql += " ORDER BY st.tabname, key_seq";

        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (owner is not null) cmd.Parameters.Add(new IfxParameter("o", owner));
        if (table is not null) cmd.Parameters.Add(new IfxParameter("t", table));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            t.Rows.Add(connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                reader.GetString(2).Trim(), reader.GetInt32(4), reader.GetString(3).Trim());
        return t;
    }

    // The FK (child) side only — unlike getImportedKeys, which (per the decompile) binds the
    // same owner restriction to both the parent and child table, this only restricts the
    // table the caller actually asked about; a parent table under a different owner still
    // matches.
    private static DataTable BuildForeignKeys(IfxConnection connection, string?[]? r)
    {
        var t = new DataTable(ForeignKeys);
        t.Columns.Add("PKTABLE_CATALOG", typeof(string));
        t.Columns.Add("PKTABLE_OWNER", typeof(string));
        t.Columns.Add("PKTABLE_NAME", typeof(string));
        t.Columns.Add("PKCOLUMN_NAME", typeof(string));
        t.Columns.Add("FKTABLE_CATALOG", typeof(string));
        t.Columns.Add("FKTABLE_OWNER", typeof(string));
        t.Columns.Add("FKTABLE_NAME", typeof(string));
        t.Columns.Add("FKCOLUMN_NAME", typeof(string));
        t.Columns.Add("KEY_SEQ", typeof(int));
        t.Columns.Add("UPDATE_RULE", typeof(string));
        t.Columns.Add("DELETE_RULE", typeof(string));
        t.Columns.Add("FK_NAME", typeof(string));
        t.Columns.Add("PK_NAME", typeof(string));

        string? owner = R(r, 1), table = R(r, 2);
        string sql =
            "SELECT pt.owner, pt.tabname, pc.colname, ft.owner, ft.tabname, fc.colname, " +
            "fk.constrname, pk.constrname, r.delrule, " + KeySeqCase("fc.colno", "fi") + " AS key_seq " +
            "FROM systables pt, syscolumns pc, sysindexes pi, sysconstraints pk, " +
            "systables ft, syscolumns fc, sysindexes fi, sysconstraints fk, sysreferences r " +
            "WHERE pt.tabid = pc.tabid AND pc.tabid = pi.tabid AND pt.tabid = pk.tabid " +
            "AND pk.constrid = r.primary AND r.constrid = fk.constrid " +
            "AND pi.idxname = pk.idxname AND fi.idxname = fk.idxname " +
            "AND ft.tabid = fc.tabid AND fc.tabid = fi.tabid AND ft.tabid = fk.tabid " +
            "AND ft.tabid > 99 AND (" + PairedPartsOrClause("pc.colno", "pi", "fc.colno", "fi") + ")";
        if (owner is not null) sql += " AND ft.owner = ?";
        if (table is not null) sql += " AND ft.tabname = ?";
        sql += " ORDER BY pt.tabname, ft.tabname, key_seq";

        using var cmd = connection.CreateInternalCommand();
        cmd.CommandText = sql;
        if (owner is not null) cmd.Parameters.Add(new IfxParameter("o", owner));
        if (table is not null) cmd.Parameters.Add(new IfxParameter("t", table));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // sysreferences.delrule: 'C' = ON DELETE CASCADE, else RESTRICT — Informix has no
            // SET NULL/SET DEFAULT/NO ACTION delete rule, and no update rule at all (matching
            // getImportedKeys' hardcoded importedKeyRestrict for UPDATE_RULE).
            string deleteRule = reader.GetString(8).Trim() == "C" ? "CASCADE" : "RESTRICT";
            t.Rows.Add(
                connection.Database, reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetString(2).Trim(),
                connection.Database, reader.GetString(3).Trim(), reader.GetString(4).Trim(), reader.GetString(5).Trim(),
                reader.GetInt32(9), "RESTRICT", deleteRule, reader.GetString(6).Trim(), reader.GetString(7).Trim());
        }
        return t;
    }
}
