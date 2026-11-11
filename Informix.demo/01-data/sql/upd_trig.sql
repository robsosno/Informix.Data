-- This file creates a table customer_log and then creates a trigger 
-- cust_trig that updates this table each time an update operation  
-- is performed on the credit_status column of the customer table.
-- The trigger is defined on the customer supertable  so that
-- update operations on the subtables (retail_customer or whlsale_customer)
-- also result in an update of customer_log table. (The scope of an update
-- on a supertable is the supertable and its subtables.) 
 

create table customer_log 
    (cust_num    int,
    cust_name    name_t,
    username     character(32),
    update_time  datetime year to minute,
    old_status   CHAR(1),
    new_status   CHAR(1) );

    create trigger cust_trig
    update of credit_status on customer
    referencing old as pre_upd
        new as post_upd  
	for each row(insert into customer_log
	    values (pre_upd.customer_num, pre_upd.customer_name, user, current,
		pre_upd.credit_status, post_upd.credit_status));
