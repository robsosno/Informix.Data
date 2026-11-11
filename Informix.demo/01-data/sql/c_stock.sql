create table stock
        (
        stock_num       smallint,
        manu_code       char(3) references manufact (manu_code),
        unit            char(4),
        description     varchar(15),
        unit_price      money(6,2),
        min_reord_qty   smallint,
        min_inv_qty     smallint,
        manu_item_num   char(20),
        unit_cost       money(6,2),
        status          char(1),
        bin_num         integer,
        qty_on_hand     smallint,
        bigger_unit     char(4),
        per_bigger_unit smallint,
                primary key (stock_num, manu_code, unit)
        );
