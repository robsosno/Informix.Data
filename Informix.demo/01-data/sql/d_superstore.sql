-- Removes everything the loader creates in superstore_demo2, so it can be loaded again
-- (the loader runs this for --recreate and ignores the errors for objects that are not there).
--
-- Sub-tables go before their super-table, and a type goes after every table and routine that
-- uses it. Triggers disappear with the tables they are defined on.

drop table cat_hits_log;
drop table customer_log;
drop table log_record;
drop table new_manu;
drop table upd_manu;
drop table manu_log;
drop table manu_operations_summary;
drop table stock_spec;
drop table ext_customer;

drop table cust_calls;
drop table call_type;
drop table items;
drop table orders;
drop table catalog;
drop table sales_rep;
drop table region;
drop table stock_discount;
drop table stock;
drop table units;
drop table manufact;
drop table retail_customer;
drop table whlsale_customer;
drop table customer;
drop table location_us;
drop table location_non_us;
drop table location;
drop table state;

drop function best_month(int);
drop function elem_position(list(datetime year to day not null), int);
drop function last_contact(int);
drop function last_contact(name_t);
drop function stock_by_manufacturer;
drop procedure stock_summary;
drop procedure manu_proc();

drop row type whlsale_t restrict;
drop row type retail_t restrict;
drop row type customer_t restrict;
drop row type loc_non_us_t restrict;
drop row type loc_us_t restrict;
drop row type location_t restrict;
drop row type ship_t restrict;
drop row type name_t restrict;
drop type percent restrict;
