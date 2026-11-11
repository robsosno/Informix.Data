CREATE ROW TYPE ship_t
        (
        date            DATE,
        weight          DECIMAL(8,2),
        charge          MONEY (6,2),
        instruct        VARCHAR(40)
        );
 
create table orders
  (
    order_num           serial8 (1001) primary key,
    order_date          date,
    customer_num        integer,
    shipping            ship_t,
    backlog             BOOLEAN,
    po_num              char(10),
    paid_date           date,
        FOREIGN KEY (customer_num) REFERENCES customer (customer_num)
  );
