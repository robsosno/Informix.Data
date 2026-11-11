# Demo data, scripts and loader

Data and scripts for the demo programs, taken from the Informix `superstore_demo2` sample
(`dbaccess/demo_ud` of an Informix sample distribution, not included here) and adapted for
this driver.

| Folder | Contents |
| ------ | -------- |
| `data/` | `.unl` files (UNLOAD format), the picture and description files for `catalog` |
| `sql/` | `create_databases.sql` (run first), `c_*.sql` table definitions, `cr_*.sql` indexes/triggers, `spl_routines.sql` + `upd_trig.sql` SPL routines and the update trigger, `d_*.sql` clean-up, `sel_*.sql` / `ins_*.sql` / `merge.sql` / `sesscoll.sql` sample statements |
| `loader/` | `SuperstoreLoader`, a .NET program that fills the database (after you create it) |

## The database

* **superstore_demo2** - the object-relational sample: named ROW types with inheritance
  (`customer` -> `retail_customer`, `whlsale_customer`; `location` -> `location_us`,
  `location_non_us`), `LIST`/`SET` columns, a `ROW` holding a `BLOB`, `CLOB`, `SERIAL8`,
  `BOOLEAN`, a `DISTINCT TYPE` (`percent`), SPL routines and triggers.
  `stock_spec` (a `BSON` column) is not part of the Informix sample; it was added for these demos.

The programs that use this database are in [`../02-native`](../02-native) and [`../03-abstraction`](../03-abstraction); see [`../README.md`](../README.md).

## Step 1 - create the database (once, on the server)

**The loader does not create the database; you must.** The server refuses `CREATE DATABASE`
(error -759) on a connection opened with a database selected, and this driver always opens one.
Run the script on the database server, with an account that may create databases:

```bash
dbaccess - sql/create_databases.sql
```

(the `-` means "no database"; copy `create_databases.sql` to the server first if you are not
working there). It creates `superstore_demo2` `WITH LOG` and grants CONNECT and
RESOURCE to PUBLIC — that is also the loader's default `--database`. If you choose another
name, pass it to the loader as `--database`. The loader stops with a message if the database
is missing or has no logging.

## Step 2 - run the loader

`run-loader.cmd` (Windows) and `run-loader.sh` (Linux/macOS/Git Bash) build and run it; every
argument is passed on. Show what would happen, without a server:

```bash
./run-loader.sh --dry-run
```

Load for real. The connection string needs no `Database`; the loader sets it. Put it in the
`INFORMIX_DEMO_CONNECTION` setting of the `.env` file in the `Informix.demo` folder (copy
`.env.example`; the file is git-ignored because it holds a password), or in an environment variable
of that name:

```bash
./run-loader.sh --recreate
```

Without `--recreate` the loader stops when a database already has tables. With it, the loader
first runs `sql/d_superstore.sql`, which drops the tables, routines and types
an earlier load created (and nothing else), then loads again.

`run-loader.sh --help` prints every option. Besides those used above: `--connection` passes the
connection string on the command line instead of through `.env`; `--no-extras` skips the indexes,
SPL routines and triggers; `--root` points at the folder holding `data/` and `sql/` when
auto-detection does not find it.

Smart large objects go to the server's default sbspace. Transactions are used when the database
is logged, which the loader requires.

What is loaded, in order: tables (`sql/c_*.sql`), rows (`data/*.unl`), `stock_spec` documents,
then indexes, SPL routines and triggers - after the rows, so loading does not fire the triggers.
`c_stock_proc.sql` (two routines for the demos: `stock_by_manufacturer` returns several rows, `stock_summary` has OUT
parameters) goes in with them. `sel_*.sql`, `merge.sql`, `ins_*.sql` (except `ins_bson.sql`) and `sesscoll.sql` are sample
statements to run by hand; the loader leaves them alone.

## Needs a server that

* has logical logging (for `WITH LOG`) and a default sbspace (`SBSPACENAME`);
* is IDS 12.10 or newer for `stock_spec` (`--no-bson` skips it).

`sql/c_extcustom.sql` (the `ext_customer` external table used by `merge.sql`) reads a file on the
server's disk, so it is created only with `--external-table`, after copying
`data/ext_customer_9.unl` to the server's `/tmp`.
