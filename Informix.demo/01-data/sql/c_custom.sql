-- create customer hierarchy

CREATE ROW TYPE name_t
        (
        first   VARCHAR(15),
        last    VARCHAR(15)
        );
 
CREATE ROW TYPE  customer_t
        (
        customer_num    SERIAL,                 -- unique customer ID
        customer_type   CHAR(1),                -- customer type:
                                                --      R = retail
                                                --      W = wholesale
        customer_name   name_t,                 -- first and last name of customer
        customer_loc    INTEGER,                -- location
        contact_dates   LIST(DATETIME YEAR TO DAY NOT NULL), -- date of contacts
        cust_discount   percent,
        credit_status   CHAR(1)                 -- D = deadbeat, L = lost, N = new,
                                                -- R = regular, P = preferred
        );
 
CREATE ROW TYPE retail_t
        (
        credit_num      CHAR(19),
        expiration      DATE
        ) UNDER customer_t;
 
CREATE ROW TYPE whlsale_t
        (
        resale_license  CHAR(15),
        terms_net       SMALLINT
        ) UNDER customer_t;
 
CREATE TABLE customer OF TYPE customer_t
        (
        PRIMARY KEY (customer_num),
        FOREIGN KEY (customer_loc) REFERENCES location (location_id),
        CHECK (customer_type IN ('R','W')),
        CHECK (credit_status IN ('D','L','R','P','N'))
        );
 
CREATE TABLE retail_customer OF TYPE retail_t UNDER customer;
 
CREATE TABLE whlsale_customer OF TYPE whlsale_t
(CHECK (terms_net >= 0 OR terms_net IS NULL))
UNDER customer;
 
