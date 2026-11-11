create table sales_rep
        (
        rep_num         SERIAL(101) PRIMARY KEY,
        name            name_t,
        region_num      INTEGER REFERENCES region (region_num),
        home_office     BOOLEAN,
        sales           SET(ROW(month DATETIME YEAR TO MONTH,
                                amount MONEY) NOT NULL),
        commission      percent
        );
