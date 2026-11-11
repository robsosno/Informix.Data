select * from stock
	where description like '%bicycle%'
		and manu_code not like 'PRC'
	order by description, manu_code;
