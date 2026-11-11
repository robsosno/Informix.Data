create table manufact
        (
        manu_code       char(3) primary key,
        manu_name       varchar(15),
        lead_time       interval day(3) TO day,
        manu_loc        integer,        
        manu_account    char(32),
        account_status  char(1),
        terms_net       smallint,
        discount        percent, 
                foreign key (manu_loc) references location (location_id)
        );
