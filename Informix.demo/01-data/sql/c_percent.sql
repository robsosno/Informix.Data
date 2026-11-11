create distinct type percent as decimal(5,5); 
drop cast (decimal(5,5) as percent);
create implicit cast (decimal(5,5) as percent);
