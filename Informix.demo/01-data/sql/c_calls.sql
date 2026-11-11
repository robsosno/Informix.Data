 create table cust_calls
  (
        customer_num    integer,
        call_dtime      datetime year to minute,
        user_id         char(18) default user,
        call_code       char(1),
        call_descr      char(240),
        res_dtime       datetime year to minute,
        res_descr       char(240),
        PRIMARY KEY (customer_num, call_dtime),
        FOREIGN KEY (customer_num) REFERENCES customer (customer_num),
        FOREIGN KEY (call_code) REFERENCES call_type(call_code)
  );
