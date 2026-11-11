namespace Informix.Data;

/// <summary>Origin for a smart-LOB <see cref="IfxSmartLOB.Seek"/>.</summary>
public enum IfxSmartLOBWhence
{
    Begin = 0,
    Current = 1,
    End = 2,
}

/// <summary>Open flags for a smart LOB (the Informix <c>LO_*</c> mode bits).</summary>
[Flags]
public enum IfxSmartLOBOpenMode
{
    None = 0,
    Append = 1,
    WriteOnly = 2,
    ReadOnly = 4,
    ReadWrite = 8,
    DirtyRead = 16,
    Buffer = 512,
    Nobuffer = 1024,
    LockAll = 4096,
    LockRange = 8192,
}

/// <summary>Lock mode for a smart-LOB byte range.</summary>
public enum IfxSmartLOBLockMode
{
    None = 0,
    Shared = 1,
    Exclusive = 2,
}

/// <summary>Where a smart-LOB file transfer reads/writes the OS file.</summary>
public enum IfxSmartLOBFileLocation
{
    None = 0,
    Server = 1,
    Client = 2,
}

/// <summary>Create-time flags for a new smart LOB.</summary>
[Flags]
public enum IfxSmartLOBCreateTimeFlags
{
    None = 0,
    Log = 1,
    NoLog = 2,
    KeepAccessTime = 8,
    DontKeepAccessTime = 16,
}
