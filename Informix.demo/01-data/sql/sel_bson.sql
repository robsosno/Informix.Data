-- Queries on the stock_spec table (BSON column).

-- Whole document as JSON text.
SELECT s.description, sp.spec::JSON
  FROM stock_spec sp, stock s
 WHERE sp.stock_num = s.stock_num
   AND sp.manu_code = s.manu_code
   AND sp.unit      = s.unit;

-- Individual fields; a dot reaches into a nested object.
SELECT s.description,
       BSON_VALUE_LVARCHAR(sp.spec, 'material')    AS material,
       BSON_VALUE_INT(sp.spec, 'weight_g')         AS weight_g,
       BSON_VALUE_INT(sp.spec, 'dims.width_mm')    AS width_mm
  FROM stock_spec sp, stock s
 WHERE sp.stock_num = s.stock_num
   AND sp.manu_code = s.manu_code
   AND sp.unit      = s.unit;

-- Filter on a field inside the document.
SELECT sp.spec_id, sp.spec::JSON
  FROM stock_spec sp
 WHERE BSON_VALUE_INT(sp.spec, 'weight_g') > 40;

-- Extract a sub-document as BSON and show it as JSON.
SELECT BSON_GET(sp.spec, 'dims')::JSON
  FROM stock_spec sp
 WHERE sp.stock_num = 313;
