// Reference output of prompts/generate-catalog-dto.md. Written by hand from the results of the
// informix-query tools (describe_table + query) and compiled against Informix.Data; a run with a
// model will phrase things differently but should reach the same types.

namespace Demo.Dto;

/// <summary>A row of table <c>catalog</c>; primary key <c>catalog_num</c>.</summary>
public sealed record CatalogDto
{
    /// <summary>catalog_num SERIAL8, primary key.</summary>
    public long CatalogNum { get; init; }

    /// <summary>stock_num SMALLINT.</summary>
    public short? StockNum { get; init; }

    /// <summary>manu_code CHAR(3).</summary>
    public string? ManuCode { get; init; }

    /// <summary>unit CHAR(4).</summary>
    public string? Unit { get; init; }

    /// <summary>advert ROW(picture BLOB, caption LVARCHAR).</summary>
    public CatalogAdvertDto? Advert { get; init; }

    /// <summary>advert_descr CLOB; the driver returns the text itself.</summary>
    public string? AdvertDescr { get; init; }
}

/// <summary>The members of <c>catalog.advert</c>, a ROW(picture BLOB, caption LVARCHAR).</summary>
public sealed record CatalogAdvertDto
{
    /// <summary>
    /// picture BLOB. Inside a ROW the driver returns the 72-byte smart-large-object locator, not the
    /// picture: read the picture itself through the BLOB API, with the locator.
    /// </summary>
    public byte[]? Picture { get; init; }

    /// <summary>caption LVARCHAR; can be an empty string.</summary>
    public string? Caption { get; init; }
}
