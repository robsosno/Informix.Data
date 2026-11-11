   create table items
        (
	item_num            smallint,
	order_num           int8 ,
	stock_num           smallint,
	manu_code           char(3),
	unit                char(4),
	quantity            smallint,
	item_subtotal       money(8,2),
	check (quantity > 0 ),
	    primary key (item_num, order_num),
	    foreign key (stock_num, manu_code, unit) references stock,
	    foreign key (order_num) references orders
	);
