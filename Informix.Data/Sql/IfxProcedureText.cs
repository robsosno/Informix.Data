namespace Informix.Data;

/// <summary>
/// Builds the <c>execute procedure name(?, ...)</c> text for a
/// <see cref="System.Data.CommandType.StoredProcedure"/> command — shared by
/// <see cref="IfxCommand"/> and <see cref="IfxBatchCommand"/>. Informix has no
/// dedicated execute-procedure opcode: the routine runs through the ordinary
/// prepare/describe/execute path, with the routine name wrapped as an
/// <c>execute procedure</c> statement carrying one <c>?</c> per input parameter
/// (the .NET port of the ODBC <c>{call ...}</c> escape translation).
/// </summary>
internal static class IfxProcedureText
{
    // One '?' per input argument; string.Create writes "?, ?, ?" directly into the result without intermediate strings.
    public static string Build(string routineName, int inputParameterCount)
    {
        string args = inputParameterCount switch
        {
            0 => "",
            1 => "?",
            _ => string.Create(inputParameterCount * 3 - 2, inputParameterCount, static (span, n) =>
            {
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) { span[0] = ','; span[1] = ' '; span = span[2..]; }
                    span[0] = '?';
                    span = span[1..];
                }
            }),
        };
        return $"execute procedure {routineName}({args})";
    }
}
