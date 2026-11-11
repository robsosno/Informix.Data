namespace SuperstoreLoader;

/// <summary>
/// One .unl file loaded into one table. <see cref="Casts"/> maps a column position to the named
/// ROW type its ROW(...) value must be cast to.
/// </summary>
internal sealed record TableLoad(string File, string Table, IReadOnlyDictionary<int, string>? Casts = null);

/// <summary>
/// What goes into each database, in dependency order: a table comes after every table its foreign
/// keys point to, and a row type or distinct type before the first table that uses it.
/// </summary>
internal static class Plan
{
    // --- superstore_demo2 -------------------------------------------------------------------

    public const string SuperstoreDrop = "d_superstore";

    public static readonly string[] SuperstoreSchema =
    [
        "c_percent",    // distinct type percent (used by state, manufact, customer, ...)
        "c_state",
        "c_loc",        // location_t hierarchy; location_us references state
        "c_manuf",
        "c_units",
        "c_stock",
        "c_stockdis",
        "c_cat",        // BLOB/CLOB in the default sbspace
        "c_custom",     // name_t, customer_t hierarchy
        "c_region",
        "c_salesreps",
        "c_orders",     // ship_t
        "c_items",
        "c_type",
        "c_calls",
    ];

    public static readonly TableLoad[] SuperstoreTables =
    [
        new("state.unl", "state"),
        new("loc_us.unl", "location_us"),
        new("loc_non_us.unl", "location_non_us"),
        new("manufact.unl", "manufact"),
        new("units.unl", "units"),
        new("stock.unl", "stock"),
        new("stockdis.unl", "stock_discount"),
        new("catalog.unl", "catalog"),      // loaded by LoadCatalog: picture and description are files
        new("retail_cust.unl", "retail_customer", new Dictionary<int, string> { [2] = "name_t" }),
        new("whlsale_cust.unl", "whlsale_customer", new Dictionary<int, string> { [2] = "name_t" }),
        new("region.unl", "region"),
        new("salesrep.unl", "sales_rep", new Dictionary<int, string> { [1] = "name_t" }),
        new("orders.unl", "orders", new Dictionary<int, string> { [3] = "ship_t" }),
        new("items.unl", "items"),
        new("call_type.unl", "call_type"),
        new("cust_calls.unl", "cust_calls"),
    ];

    /// <summary>Added by this demo, not by Informix: BSON documents attached to stock items.</summary>
    public const string BsonSchema = "c_bson";
    public const string BsonData = "ins_bson";
    public const string BsonTable = "stock_spec";

    /// <summary>Created once the data is in, so loading does not fire the triggers.</summary>
    public static readonly string[] SuperstoreExtras = ["cr_index", "spl_routines", "c_stock_proc", "cr_trig", "upd_trig"];

    public const string ExternalTableSchema = "c_extcustom";
}
