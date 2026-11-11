-- This file creates a table named cat_hits_log and then creates a trigger 
-- named cat_hits_trig that updates this table each time a select operation
-- is performed on the advert column  of the catalog table.


CREATE TABLE cat_hits_log
(
stock_num INT,
manu_code CHAR(3),
access_time DATETIME YEAR TO MINUTE,
user CHAR(32)
);

CREATE TRIGGER cat_hits_trig SELECT OF advert ON catalog
REFERENCING OLD AS hit
FOR EACH ROW (INSERT INTO cat_hits_log
VALUES ( hit.stock_num, hit.manu_code, CURRENT, USER));
