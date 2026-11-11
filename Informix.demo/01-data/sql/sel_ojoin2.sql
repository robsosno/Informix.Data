select c.customer_num, c.lname, o.order_num, 
       i.stock_num, i.manu_code, i.quantity
       from customer c, outer (orders o, items i)
       where c.customer_num = o.customer_num
		and o.order_num = i.order_num
		and manu_code IN ('KAR', 'SHM')
       order by lname;
