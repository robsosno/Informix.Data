-- SPL routines added for the driver demos (not part of the Informix sample).
--
-- stock_by_manufacturer returns SEVERAL rows (WITH RESUME), so the demos can unload the result
-- of a procedure the same way as that of a table. stock_summary has OUT parameters.

create function stock_by_manufacturer (p_manu char(3))
    returning smallint as stock_num,
              varchar(15) as description,
              char(4) as unit,
              money(6,2) as unit_price,
              smallint as qty_on_hand;

    define v_num smallint;
    define v_descr varchar(15);
    define v_unit char(4);
    define v_price money(6,2);
    define v_qty smallint;

    foreach select stock_num, description, unit, unit_price, qty_on_hand
            into v_num, v_descr, v_unit, v_price, v_qty
            from stock
            where manu_code = p_manu
            order by stock_num, unit
        return v_num, v_descr, v_unit, v_price, v_qty with resume;
    end foreach;
end function;

create procedure stock_summary (p_manu char(3), out p_items int, out p_value decimal(14,2))
    select count(*), nvl(sum(unit_price * qty_on_hand), 0)
        into p_items, p_value
        from stock
        where manu_code = p_manu;
end procedure;
