-- Creates the demo database. Run this ONCE, on the database server, before the loader:
--
--     dbaccess - create_databases.sql
--
-- (the "-" means "no database selected"). Use an account that may create databases.
--
-- Not part of the original Informix sample. The loader cannot do this itself: the server refuses
-- CREATE DATABASE (error -759) on a connection that was opened with a database, and this
-- driver always opens one.
--
-- The database needs transaction logging: the demos use transactions, and a smart large object
-- uploaded by a statement that then fails is only reclaimed by a rollback. The loader checks it.
-- The default dbspace and locale are used; the demo data is plain ASCII.
--
-- If you pick another name, pass it to the loader as --database.

create database superstore_demo2 with log;
grant connect to public;
grant resource to public;
close database;
