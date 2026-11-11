--- CROSS JOIN

select c.customer_num, c.customer_name,
       u.call_dtime, u.call_descr
       from customer c cross join cust_calls u
       where c.customer_num = u.customer_num
       order by 1;

--- CROSS JOIN + ANSI Inner Join

select c.customer_num, o.order_num, o.order_date
       from customer c cross join ( orders o join items i
       on o.order_num = i.order_num);


--- CROSS JOIN + ANSI Left Join

select c.customer_num, o.order_num, o.order_date
       from customer c cross join ( orders o left join items i
       on o.order_num = i.order_num);

select c.customer_num, o.order_num, o.order_date
       from customer c cross join ( orders o left outer join items i
       on o.order_num = i.order_num);

--- CROSS JOIN + ANSI Right Join

select c.customer_num, o.order_num, o.order_date
       from customer c cross join ( orders o right join items i
       on o.order_num = i.order_num);

select c.customer_num, o.order_num, o.order_date
       from customer c cross join ( orders o right outer join items i
       on o.order_num = i.order_num);



--- LEFT JOIN

select c.customer_num, c.customer_name, 
        u.call_dtime, u.call_descr
       from customer c left join cust_calls u
       on c.customer_num = u.customer_num
       order by 1;

--- LEFT OUTER JOIN

select c.customer_num, c.customer_name, 
        u.call_dtime, u.call_descr
       from customer c left outer join cust_calls u
       on c.customer_num = u.customer_num
       order by 1;

--- LEFT JOIN + ANSI Inner Join

select c.customer_num, o.order_num, o.order_date
       from customer c left join ( orders o inner join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;

--- LEFT JOIN + ANSI Left Join

select c.customer_num, o.order_num, o.order_date
       from customer c left join ( orders o left join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;


--- LEFT JOIN + ANSI Right Join

select c.customer_num, o.order_num, o.order_date
       from customer c left join ( orders o right join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;



--- RIGHT JOIN

select c.customer_num, c.customer_name,
        u.call_dtime, u.call_descr
       from customer c right join cust_calls u
       on c.customer_num = u.customer_num
       order by 1;

--- RIGHT OUTER JOIN

select c.customer_num, c.customer_name, 
        u.call_dtime, u.call_descr
       from customer c right outer join cust_calls u
       on c.customer_num = u.customer_num
       order by 1;

--- RIGHT JOIN + ANSI Inner Join

select c.customer_num, o.order_num, o.order_date
       from customer c right join ( orders o inner join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;

--- RIGHT JOIN + ANSI Left Join

select c.customer_num, o.order_num, o.order_date
       from customer c right join ( orders o left join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;

--- RIGHT JOIN + ANSI Right Join

select c.customer_num, o.order_num, o.order_date
       from customer c right join ( orders o right join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;

--- INNER JOIN

select c.customer_num, c.customer_name,
       u.call_dtime, u.call_descr
       from customer c inner join cust_calls u
       on c.customer_num = u.customer_num
       order by 1;

--- INNER JOIN + ANSI Inner Join

select c.customer_num, o.order_num, o.order_date
       from customer c inner join ( orders o inner join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;

--- INNER JOIN + ANSI Left Join

select c.customer_num, o.order_num, o.order_date
       from customer c inner join ( orders o left join items i
       on o.order_num = i.order_num)  on c.customer_num = o.customer_num;

--- INNER JOIN + ANSI Right Join

select c.customer_num, o.order_num, o.order_date
       from customer c inner join ( orders o right join items i
       on o.order_num = i.order_num) on c.customer_num = o.customer_num;

-- Derived table (table expression) queries

select stock_num, manu_code from 
 ( select * from items where quantity > 3) as vtab 
 where vtab.stock_num =5;

-- Derived table with column names

select * from 
 ( select rowid, customer_num, customer_loc from customer ) as vtab(c0,c1,c2)
 where vtab.c1 between 110 and 120 order by vtab.c0;

-- View using a derived table

create view manu_info as select vtab1.manu_name, vtab2.city, vtab2.country from
 ( select * from manufact where terms_net > 25 ) as vtab1 , 
 (select * from location) as vtab2 
 where vtab1.manu_loc = vtab2.location_id;


-- Derived table in a left outer join

select c.customer_num, c.customer_name, u.call_dtime, u.call_descr from
 ( select customer_num,customer_name from customer ) c
 left outer join
 ( select call_dtime,call_descr,customer_num from cust_calls ) u
 on c.customer_num = u.customer_num 
 order by 1;

-- Derived table with innerjoin

select c.customer_num, o.order_num, o.order_date from
 ( select customer_num from customer ) c
 inner join ( ( select * from orders ) o
 inner join ( select order_num from items ) i
 on o.order_num = i.order_num) on c.customer_num = o.customer_num;

-- Derived table with union 

select * from 
 ( select distinct stock_num, manu_code from stock where unit_price < 25.00
 union 
 select stock_num, manu_code from items where quantity > 3 ) as vtab(col1,col2)
 where vtab.col2 = 'KAR' and vtab.col1 > 300 order by 1;

-- Table iterator function

create procedure iter_func(inp_arg int)
returning integer;
define retval integer;
 foreach select region_num*inp_arg into retval from region
 return retval with resume;
end foreach;
end procedure;

-- Using the table iterator function in queries

select * from table ( iter_func(1));

select col1 from table ( iter_func(2)) tab1(col1)
group by col1
order by col1;

select count(*) from table ( iter_func(2));

select col1 from table ( iter_func((select min(region_loc) from region))) tab1(col1);

