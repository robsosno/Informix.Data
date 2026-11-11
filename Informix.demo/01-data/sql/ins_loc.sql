-- This script adds new locations to the location_us and
-- location_non_us tables.

execute procedure new_us_loc ('W', 'RollerBlade World', 
'list{''1414 Stauton Avenue''}',  'Linder Park', 'USA', 'CA', 
'96044', '417-653-1993' );

execute procedure new_us_loc ('W', 'MidWest Sports', 
'list{''1801 Eastern Drive''}',  'West Lafayette', 'USA', 'IN', 
'47933', '317-555-6870' );

execute procedure new_non_us_loc ('W', 'Super Soccer Store', 
'list{''Lindenstrasse 52''}', 'Ulm', 
'Germany', '', 'U-14159', '49-8282-7373-4' );

execute procedure new_non_us_loc ('W', 'Sportsworld', 
'list{''Via Paleocopa 72A''}',  'Padua', 
'Italy', '', '381-99650', '39-14-773-1854' );
