create table stock_discount
        (
        discount_id     serial,
        stock_num       smallint,
        manu_code       char(3),
        unit            char(4),
        unit_discount   percent,
        start_date      date,
        end_date        date,
           foreign key (stock_num, manu_code, unit)
                references stock (stock_num, manu_code, unit)
        );
