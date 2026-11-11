-- create a table that holds semi-structured product specifications as BSON.
--
-- Not part of the original Informix superstore_demo2; added for the driver
-- demos. BSON (and JSON) are built-in types from IDS 12.10, so this script
-- needs a server of that version or newer. Large documents are stored as
-- smart large objects in the default sbspace (SBSPACENAME in onconfig).

CREATE TABLE stock_spec
        (
        spec_id         SERIAL8 primary key,
        stock_num       SMALLINT,
        manu_code       CHAR(3),
        unit            CHAR(4),
        spec            BSON,
        FOREIGN KEY (stock_num, manu_code, unit) REFERENCES stock
        );
