--- MERGE 
--- Typical MERGE statement
MERGE INTO customer c
USING ext_customer e
ON c.customer_name=e.customer_name AND c.customer_type=e.customer_type
WHEN MATCHED THEN
UPDATE SET c.cust_discount = e.cust_discount,
           c.credit_status = e.credit_status
WHEN NOT MATCHED THEN
INSERT
        (c.customer_type, c.customer_name, c.customer_loc, c.contact_dates, c.cust_discount, c.credit_status)
        VALUES
        (e.customer_type, e.customer_name, e.customer_loc, e.contact_dates, e.cust_discount, e.credit_status)
;

-- update join - here only update clause is specified
MERGE INTO customer c
USING ext_customer e
ON c.customer_name=e.customer_name AND c.customer_type=e.customer_type
WHEN MATCHED THEN
UPDATE SET c.cust_discount = e.cust_discount,
           c.credit_status = e.credit_status
;

-- delete join - here only delete clause is specified
MERGE INTO customer c
USING ext_customer e
ON c.customer_name=e.customer_name AND c.customer_type=e.customer_type
WHEN MATCHED THEN
DELETE
;

-- Only INSERT clause is specified
MERGE INTO customer c
USING ext_customer e
ON c.customer_name=e.customer_name AND c.customer_type=e.customer_type
WHEN NOT MATCHED THEN
INSERT
        (c.customer_type, c.customer_name, c.customer_loc, c.contact_dates, c.cust_discount, c.credit_status)
        VALUES
        (e.customer_type, e.customer_name, e.customer_loc, e.contact_dates, e.cust_discount, e.credit_status)
;

--The WHEN MATCHED and WHEN NOT MATCHED clauses can come in any order.
MERGE INTO customer c
USING ext_customer e
ON c.customer_name=e.customer_name AND c.customer_type=e.customer_type
WHEN NOT MATCHED THEN
INSERT
        (c.customer_type, c.customer_name, c.customer_loc, c.contact_dates, c.cust_discount, c.credit_status)
        VALUES
        (e.customer_type, e.customer_name, e.customer_loc, e.contact_dates, e.cust_discount, e.credit_status)
WHEN MATCHED THEN
UPDATE SET c.cust_discount = e.cust_discount,
           c.credit_status = e.credit_status
;

-- derived table query in the USING clause
MERGE INTO customer c
USING (select * from ext_customer e1 where e1.credit_status='R' ) e
ON c.customer_name=e.customer_name AND c.customer_type=e.customer_type
WHEN NOT MATCHED THEN
INSERT
        (c.customer_type, c.customer_name, c.customer_loc, c.contact_dates, c.cust_discount, c.credit_status)
        VALUES
        (e.customer_type, e.customer_name, e.customer_loc, e.contact_dates, e.cust_discount, e.credit_status)
WHEN MATCHED THEN
UPDATE SET c.cust_discount = e.cust_discount,
           c.credit_status = e.credit_status
;

