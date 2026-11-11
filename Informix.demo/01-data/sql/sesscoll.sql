CREATE TABLE msgs (
        lang    char(32),
        number  integer,
        message nchar(255));


--
-- Create indexes on the table.
-- Indexes are collation aware.  IDS stores the collation under which an
-- index is created.  This gives the ability to create multiple indexes
-- on a set of columns using different collations.  This is accomplished by
-- setting the session collation accordingly, before the indexes are
-- created.  When data is selected or manipulated, the database server
-- will select the index that is appropriate for the current locale.
-- If no such index is found, then the optimizer will ignore indexes for
-- the current operation.
--
-- Stored procedures, triggers and constraints are also collation aware.
-- For information on how stored procedures, triggers and constraints behave
-- under different session collations, please refer to the SQL reference
-- manual.
--

SET NO COLLATION;       -- This sets the session collation to the default
                        -- en_us.8859-1, assuming that DB_LOCALE was not set
                        -- differently.

CREATE INDEX idxmsgs_enus on msgs (message);

SET COLLATION 'fr_fr.8859-1';   -- Create an index using fr_fr.8859-1.
CREATE INDEX idxmsgs_frfr on msgs (message);
SET NO COLLATION;

-- We will not create an index using de_de.8859-1.  When data is selected
-- with de_de.8859-1 as the session collation, the database server will
-- not use indexes for that operation.
--

