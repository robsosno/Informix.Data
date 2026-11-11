create table region
        (
        region_num      serial primary key,
        region_name     varchar(20) unique,
        region_loc      integer references location (location_id)
        );
