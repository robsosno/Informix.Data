-- create location hierarchy
 
CREATE ROW TYPE location_t
        (
        location_id     SERIAL,
        loc_type        CHAR(2),
        company         VARCHAR(20),
        street_addr     LIST(VARCHAR(25) NOT NULL),
        city            VARCHAR(25),
        country         VARCHAR(25)
        );
 
CREATE ROW TYPE loc_us_t
        (
        state_code      CHAR(2),
        zip             ROW (code INTEGER, suffix SMALLINT),
        phone           CHAR(18)
        ) UNDER location_t;

CREATE ROW TYPE loc_non_us_t
        (
        province_code   CHAR(2),
        zipcode         CHAR(9),
        phone           CHAR(15)
        ) UNDER location_t;

CREATE TABLE location OF TYPE location_t
        (PRIMARY KEY (location_id));

CREATE TABLE location_us OF TYPE loc_us_t
        (FOREIGN KEY (state_code)  REFERENCES state (code))
        UNDER location;

CREATE TABLE location_non_us OF TYPE loc_non_us_t UNDER location;
