# 04-ai: an AI assistant that queries Informix, without seeing the connection string

An MCP server (`InformixMcp`, built on Informix.Data) that lets an AI assistant such as Claude Code
list tables, describe them and run SELECTs on an Informix database; a skill that teaches the
assistant how to use it; and a prompt that shows it at work - generate a C# DTO for the table
`catalog` from the real schema.

Two requirements shaped it:

* the connection string must not be written in the skill, in the prompt, or anywhere the assistant
  can read it - it lives in a `.env` file that only the server process reads;
* the connection is **Select Only** and every transaction is **read-only**.

## Why read-only

The purpose of the read-only MCP server is to rule out mistakes that can happen when working with
an LLM. They can come from mutual misunderstanding between the AI and the human, or from the
model's hallucination - a confidently wrong `UPDATE`, `DELETE` or `DROP` that nobody meant to run.
Therefore it is essential to work with databases through an MCP server that guarantees database
safety: whatever the assistant asks for, the server can only read.

## Skill or MCP?

A skill alone cannot do it. A skill is instructions, plus perhaps a script the assistant runs
through a shell. Whatever that script can read, the assistant can read: it runs as the same user,
and the permission it needs (`Bash(dotnet run ...)`) lets it run any similar command. A skill cannot
carry deny rules either. So the secret would be hidden only by the assistant's good manners.

An MCP server is a separate process. It loads `.env` itself; the assistant sends tool calls
(`query`, `describe_table`, `list_tables`) and gets results back - nothing else crosses. The
tool surface is three read-only tools, not a shell. The result is split in two:

| Part | Holds |
| ---- | ----- |
| `InformixMcp/` (MCP server) | the secret, the read-only enforcement, error scrubbing |
| `.claude/skills/informix-query/` (skill) | the know-how: workflow, Informix SQL notes, mapping rules, "never look for credentials" |

## Layout

```
04-ai/
├── InformixMcp/                the MCP server (stdio), net10.0, ModelContextProtocol SDK + Informix.Data
├── .claude/
│   ├── settings.json           deny rules: the assistant may not read or edit .env
│   └── skills/informix-query/  the skill
├── prompts/generate-catalog-dto.md   the demo prompt
├── generated/CatalogDto.cs     reference output of that prompt (hand-written, compiled, checked against live rows)
├── .mcp.json                   how Claude Code starts the server
├── .env.example                copy to .env (git-ignored)
└── .gitignore
```

## Set it up

1. The database needs transaction logging (a read-only transaction needs one) and the demo data:
   see [../01-data/README.md](../01-data/README.md). Any database works for the tools; the prompt
   uses the `catalog` table of `superstore_demo2`.
2. Create the connection file and fill it in (the server adds `Database` handling, nothing else):

   ```bash
   cp .env.example .env
   ```

3. Build the server (Claude Code starts the built DLL, so that `dotnet`'s own output never lands on
   the protocol stream):

   ```bash
   dotnet build InformixMcp
   ```

4. Start Claude Code **in this folder** - `.mcp.json`, the skill and the deny rules are found from
   here - and approve the project's `informix` server when asked:

   ```bash
   claude
   ```

## Run the demo

Paste the contents of [prompts/generate-catalog-dto.md](prompts/generate-catalog-dto.md), or:

```bash
claude "$(cat prompts/generate-catalog-dto.md)"
```

The assistant calls `describe_table catalog`, writes `generated/CatalogDto.cs` and checks it against
three rows from `query`. It finds out on the way that the `advert` ROW holds a BLOB and a caption,
that inside a ROW the driver returns the BLOB member as the 72-byte smart-large-object locator
rather than the picture, and that the caption can be an empty string. Compare with
[generated/CatalogDto.cs](generated/CatalogDto.cs).

Other things to ask: *"which tables reference `stock`?"*, *"write a repository interface for
`orders` and its items"*, *"how many customers per state?"*, *"what is wrong with the data in
`location_non_us`?"*.

## The tools

| Tool | Does |
| ---- | ---- |
| `list_tables(nameContains?)` | user tables and views: name, kind, column count, approximate rows |
| `describe_table(table)` | per column: Informix type, the .NET type the driver returns, nullability, size / precision, primary key |
| `query(sql, maxRows = 100)` | one SELECT; rows as JSON; at most 1000 rows, 30 s; large objects replaced by a placeholder |

## What keeps it read-only, and what keeps the secret

| Layer | What it does | Where |
| ----- | ------------ | ----- |
| Tool checks | `query` takes one statement that starts with SELECT and has no `INTO` | `InformixTools.CheckSelect` |
| `Select Only=true` | the driver prepares and describes every statement and rejects all that are not SELECT (-878) - UPDATE, DELETE, `SELECT INTO`, `EXECUTE FUNCTION` were rejected on the test server | forced by `ServerSettings` |
| `Read Only=true` + `BeginTransaction(.., isReadOnly: true)` | every statement runs in `SET TRANSACTION READ ONLY`, rolled back at the end | `Database.Run` |
| Forced keywords | both keywords are set after `.env` is read, so editing `.env` cannot loosen them | `ServerSettings.Load` |
| Scrubbing | the connection string, password, user and host are replaced with `***` in every error text | `ServerSettings.Scrub` |
| Deny rules | the assistant's own tools may not read or edit `.env` | `.claude/settings.json` |
| No tool exposes the connection | no tool takes or returns it; "not configured" errors tell the assistant to ask the user | `InformixTools` |

## Limits - read before pointing it at a real database

* **The deny rules are enforced by Claude Code for its own tools.** Patterns on shell commands are
  best-effort: an assistant that can run arbitrary commands can still read `.env` another way. For
  a hard boundary keep the server out of the assistant's reach - another OS user, a container, or a
  remote MCP server - or use the Claude Code sandbox with filesystem read denies.
* **`Select Only` is a guard against mistakes, not a security boundary.** Use a database account
  that has only SELECT privileges; that is the control that holds when everything above fails.
* **Whatever `query` returns goes to the model.** Do not point it at data that may not leave your
  network. Rows are untrusted input - text in a column can try to give the assistant instructions;
  the skill says to ignore it, which is a mitigation, not a guarantee.
* The read-only transaction needs a database with logging; on one without, every call fails.
* Environment variable `INFORMIX_MCP_CONNECTION` (and `INFORMIX_MCP_ENV`, a path to the env file)
  work in place of `.env` - useful when a secret store sets the environment of the server process.
