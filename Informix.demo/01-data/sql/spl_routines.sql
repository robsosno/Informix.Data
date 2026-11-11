-- The best_month function returns a month date from the sales
-- collection column in the sales_rep table for the element
-- expressing the highest monthly sales amount.


CREATE FUNCTION best_month (r_num INT )
   RETURNING DATETIME YEAR TO MONTH AS procmonth;

   DEFINE month_var DATETIME YEAR TO MONTH;
   DEFINE list_var SET(ROW(mnth DATETIME YEAR TO MONTH, amt MONEY) NOT NULL);
   DEFINE max_amt_var MONEY;
   DEFINE amt_var MONEY;

   LET amt_var = 0.00;    -- initializes amount_var

   SELECT  sales INTO list_var FROM sales_rep
        WHERE rep_num = r_num;

   FOREACH  coll_cursor FOR
        SELECT m_sales.amt INTO max_amt_var FROM TABLE(list_var) m_sales
        IF (max_amt_var > amt_var)
                THEN LET  amt_var = max_amt_var;
        END IF;
   END FOREACH;
  SELECT m_sales.mnth INTO month_var FROM TABLE(list_var) m_sales
        WHERE m_sales.amt = amt_var;

   RETURN  month_var;

END FUNCTION;

-- The elem_position function accepts two arguments and returns an element
-- (a date value) from the contact_dates column of the customer, retail_customer,
--  or whlsale_customer table. The second argument you specify indicates the ordinal
-- position of the element that the function returns.

-- The following example shows how you can use the function in an SQL statement:

--   SELECT elem_position(contact_dates, 2) FROM retail_customer
--       WHERE customer_num = 102;

-- The following SPL statement creates the elem_position function:


create function elem_position(my_coll list(datetime year to day  not null),
                        elem_no int )
returning datetime year to day as procdate;

  define card integer;
  define spl_date datetime year to day;
  define fetch_cnt integer;

  let fetch_cnt = 1;

  let card = (select count(*) from table(my_coll));

  if ( elem_no <= 0 ) or (card < elem_no) then
    return;
  end if;

  foreach cur1 for select * into spl_date from table(my_coll)
    if ( fetch_cnt < elem_no ) then
      begin
        let fetch_cnt = fetch_cnt + 1;
        continue foreach;
      end;
    else
      return spl_date;
    end if;
  end foreach;
end function;

-- The last_contact function accesses the individual elements of a
-- collection column and returns the element with the most recent
-- customer contact date.

--  The function assumes that the list is not empty!
-- If the collection evaluated is empty the function returns the
-- initial value for newest_date_var.

-- Examples that show how to use the last_contact function:

--      SELECT last_contact(customer_num)
--          FROM retail_customer
--           WHERE customer_num = 120;

--       EXECUTE FUNCTION last_contact(105);

-- The following SPL statement creates the last_contact function:


CREATE FUNCTION last_contact(c_num INT)
   RETURNING DATETIME YEAR TO DAY AS PROCNEWDATE;

   DEFINE date_var DATETIME YEAR TO DAY;
   DEFINE  list_var LIST(DATETIME YEAR TO DAY NOT NULL);
   DEFINE newest_date_var DATETIME YEAR TO DAY;

   LET newest_date_var = DATE('01/01/1900');    -- initializes newest_date_var

   SELECT contact_dates INTO list_var FROM customer
        WHERE customer_num = c_num;

   FOREACH  coll_cursor FOR
        SELECT * INTO date_var FROM TABLE(list_var)
        IF (date_var > newest_date_var)
                THEN LET newest_date_var = date_var;
        END IF;
   END FOREACH;

   RETURN newest_date_var;

END FUNCTION;

-- This SPL routine overloads the last_contact function
-- so that users can  specify arguments of either  an
-- INTEGER value (customer_num)  or  name_t row type value
-- (customer_name) to access the collection element
-- with the most recent  cusotmer contact date.

--  The function assumes that the list is not empty!
-- If the collection evaluated is empty the function returns the
-- initial value for newest_date_var.

CREATE FUNCTION last_contact(cust_name name_t)
   RETURNING DATETIME YEAR TO DAY AS PROCNEWDATE;

   DEFINE date_var DATETIME YEAR TO DAY;
   DEFINE  list_var LIST(DATETIME YEAR TO DAY NOT NULL);
   DEFINE newest_date_var DATETIME YEAR TO DAY;

   LET newest_date_var = DATE('01/01/1900');    -- initializes newest_date_var

   SELECT contact_dates INTO list_var FROM customer
        WHERE customer_name = cust_name;

   FOREACH  coll_cursor FOR
        SELECT * INTO date_var FROM TABLE(list_var)
        IF (date_var > newest_date_var)
                THEN LET newest_date_var = date_var;
        END IF;
   END FOREACH;

   RETURN newest_date_var;

END FUNCTION;
