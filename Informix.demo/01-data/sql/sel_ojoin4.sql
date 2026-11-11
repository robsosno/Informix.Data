select c.customer_num, lname, o.order_num, 
       order_date, call_dtime
       from customer c, outer orders o, outer cust_calls x
       where c.customer_num = o.customer_num
		and c.customer_num = x.customer_num
       order by 1
       into temp service;
