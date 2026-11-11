-- This script adds two new customers to the retail_customer table
-- and two new customers to the whlsale_customer table.

execute procedure new_retail ('R', 'George', 'Pascal', 7, '1991-04-14', '1997-05-02', 0.0, 'R', '4322 3279 4734 3210', '12/06/1998'); 

execute procedure new_retail ('R', 'Betty', 'Walsh', 30, '1992-10-12', '1996-11-15', 0.0, 'R', '4344 4632 4985 2139', '07/15/1999'); 

execute procedure new_whlsale ('W','Frieda','Kenyon',5,'1994-09-02','1995-05-10','0.3','R','MM80910105','50'); 
 
execute procedure new_whlsale ('W','Francis','Bacon',14,'1992-06-02','1997-08-12','0.0','R','GD2642821','30'); 
