
-- Select data using different collation 

SET COLLATION 'de_de.8859-1';

SELECT message FROM msgs  WHERE lang = 'de_de'
       ORDER BY message;

SET COLLATION 'fr_fr.8859-1';

SELECT message FROM msgs WHERE lang = 'fr_fr'
       ORDER BY message;

SET NO COLLATION;

SELECT message FROM msgs WHERE lang = 'en_us'
       ORDER BY message;

