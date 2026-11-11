create table log_record
        (item_num    smallint,
        ord_num      integer,
        username     character(32),
        update_time  datetime year to minute,
        old_qty      smallint,
        new_qty      smallint);
 
create trigger upqty_i
update of quantity on items
referencing old as pre_upd
        new as post_upd
for each row(insert into log_record
    values (pre_upd.item_num, pre_upd.order_num, user, current,
        pre_upd.quantity, post_upd.quantity));


create table new_manu
	(
	manu_name  varchar(15),
        manu_account    char(32),
        manu_loc        integer,
        discount        percent
	);

create table upd_manu
	(
        manu_account    char(32),
	old_terms_net   smallint,
        new_terms_net   smallint
	);

create table manu_log
	(
	manu_code	char(3),
        manu_account    char(32),
	trans_type	char(10)
	);

create table manu_operations_summary
	(
	desc		char(32)
	);



create procedure manu_proc()
referencing OLD as pre NEW as post for manufact;

	if (INSERTING) then
	-- Implement a business rule to increase discount for new manufacturers
		let post.discount = post.discount * 1.2;  
		insert into new_manu values 
                 (post.manu_name,post.manu_account,post.manu_loc,post.discount);
	end if

	if (UPDATING) then
        insert into upd_manu values(pre.manu_account,pre.terms_net,post.terms_net);
	end if

	if (SELECTING) then
	insert into manu_log values ( pre.manu_code,pre.manu_account,'SELECT');
	end if

	if (DELETING) then
	insert into manu_log values ( pre.manu_code,pre.manu_account,'DELETE');
	end if


end procedure;

-- Multiple triggers on the same Insert event on manufact table

create trigger manu_ins_trig1 insert on manufact 
before ( insert into manu_operations_summary values ('New Manufacturer'))
for each row(execute procedure manu_proc() with trigger references);

create trigger manu_ins_trig2 insert on manufact referencing NEW as new
for each row 
( insert into manu_log values (new.manu_code,new.manu_account,'INSERT'));

--- Multiple triggers on the same Update event on manufact table

create trigger manu_upd_trig1 update on manufact
before 
( insert into manu_operations_summary values ('Manufacturer info changed'))
for each row
    (execute procedure manu_proc() with trigger references);

create trigger manu_upd_trig2 update on manufact 
referencing OLD as old New as new
for each row 
 ( insert into manu_log values (new.manu_code,new.manu_account,'UPDATE'));

