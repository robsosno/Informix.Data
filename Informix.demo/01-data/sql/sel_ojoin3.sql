select c.customer_num, lname, o.order_num, 
       stock_num, manu_code, quantity
       from customer c, outer (orders o, outer items i)
       where c.customer_num = o.customer_num
		and o.order_num = i.order_num
		and manu_code IN ('KAR', 'SHM')
       order by lname;
