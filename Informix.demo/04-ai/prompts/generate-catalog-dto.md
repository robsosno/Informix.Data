Use the informix-query skill.

Generate a C# DTO for the table `catalog` of the connected database.

1. Look at the table with `describe_table` - do not guess the columns or their types.
2. Write the result to `generated/CatalogDto.cs`:
   * namespace `Demo.Dto`, file-scoped, one `public sealed record CatalogDto` with `init` properties
     in PascalCase, in column order;
   * C# types exactly as the driver returns them (`clrType`), nullable (`?`) where the column allows NULL;
   * an XML doc comment on every property with the column name and its Informix type, and on the
     type the table name and its primary key;
   * the ROW column becomes its own nested record, one property per member of the ROW type.
3. Look at three real rows with `query` and check the DTO against them. Tell me anything the schema
   alone did not show (padding, empty string versus NULL, what a large-object column holds) and
   whether you changed the DTO because of it.

Use only the tools of the skill. You do not need, and will not be given, a connection string.
