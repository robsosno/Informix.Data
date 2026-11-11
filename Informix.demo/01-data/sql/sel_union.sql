select distinct stock_num, manu_code
	from stock
	where unit_price < 25.00

union

select stock_num, manu_code
	from items
	where quantity > 3; 
