-- An external table reads a file on the SERVER's disk, not on the client.
-- The original path assumed the server had the Informix demo directory
-- installed; here the file is expected in /tmp, so copy
-- data/ext_customer_9.unl there first (or adjust the path below).
create external table ext_customer sameas  customer
        using (
                datafiles ('disk:/tmp/ext_customer_9.unl'),
                numrows 3
                );
