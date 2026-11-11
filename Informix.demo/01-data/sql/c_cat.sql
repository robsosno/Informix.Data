-- create table that uses smart large objects (CLOB & BLOB) to
-- store the catalog advertisement data.
--
-- The original script named a dedicated sbspace (PUT ... IN (s9_sbspc)).
-- It is omitted here, so the smart large objects go to the server's default
-- sbspace (SBSPACENAME in onconfig).

CREATE TABLE  catalog
        (
        catalog_num     SERIAL8 (10001) primary key,
        stock_num       SMALLINT,
        manu_code       CHAR(3),
        unit            CHAR(4),
        advert          ROW (picture BLOB, caption LVARCHAR),
        advert_descr    CLOB,
	FOREIGN KEY (stock_num, manu_code, unit) REFERENCES stock
        );
