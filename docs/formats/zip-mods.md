# ZIP mods and the mods folder

How the game reads its generated content (baked level blocks, mission
files, tables) and mods. Code (retroeng repo): `src/RetroEng.Core/Content/` (`ZipPack`,
`ZipPackWriter`, `ZipFormat`, `VirtualFileSystem`, `ContentRoot`,
`ZipUnpack`). Game-agnostic: a ZIP does not know what its entries mean.

Every archive is a **standard ZIP**, the system mod included, in the way
Quake 3's `.pk3` is: 7-Zip, Explorer or `tar -tf` open it. A mod is a ZIP
or a folder; there is no other container format. Older builds kept the
generated content in their own container, `mods/<dataset>.pak` (RPAK); it
is no longer read, and the game deletes it (logged) once
`mods/<dataset>.zip` is written, since it was the engine's own cache and
never a player's file.

## What the engine reads

- Entries stored (method 0) or Deflate (method 8). An encrypted entry,
  another method (Deflate64, bzip2, LZMA...) or a split archive is refused
  when the ZIP opens (`ZipPackError.Unsupported`, naming the entry).
- ZIP64 (archives over 4 GB or over 65535 entries): end record, locator and
  the per-entry extended information.
- Names are UTF-8. Directory entries are skipped; backslashes become
  slashes; a name with `..` or a root is refused. Paths are compared
  ignoring case; of two entries with the same path the first wins.
- Data descriptors (flag bit 3) are fine: sizes come from the central
  directory.

## The engine's manifest

A ZIP written by `ZipPackWriter` carries one extra entry, `manifest.json`
in the root (written last), with what a ZIP does not hold itself: each
entry's SHA-256 and the free-form **source hash** a builder compares to
decide whether to rebuild the entry (e.g. a bake version plus a hash of
the inputs). LF line ends, one entry per line, entries in ordinal path
order, `"source"` left out when empty:

```
{"format":"retroeng-zip-manifest","version":1,"entries":[
{"path":"maps/MAP.210.block","sha256":"9a8f…","source":"mapblock 2 tool 1 md5 9074…"},
{"path":"missions/M01.json","sha256":"a0c1…","source":"mission …"}
]}
```

- The reader hides it from the entry list and attaches its data to the
  entries (`ZipPackEntry.ContentHash`, `SourceHash`); an entry it does not
  list has no SHA-256 and the source hash "".
- A root `manifest.json` without this `format` string is an ordinary file
  of the mod. One that has it and does not parse (or is another version)
  makes the ZIP unreadable (`DirectoryCorrupt`), so a builder rebuilds it.
- For SkyNET's 130 entries the manifest is about 22 KB.

## Reading

- `ZipPack.Open` reads the end record (and the ZIP64 one when needed), the
  central directory once, and the manifest; each entry is read later with
  positioned reads (`RandomAccess`) on one shared handle, so entries load
  independently and from any number of threads. A stored entry is one
  read of its bytes; a Deflate one is inflated on each read. The data
  offset is taken from the entry's local header on first read and kept.
- `ZipMount` mounts a `ZipPack` in the `VirtualFileSystem`; user ZIPs and
  the system ZIP use the same reader.
- Rejected cleanly with `ZipPackException.Error`: `NotAZip`, `Truncated`
  (it starts like a ZIP but its end is gone, or an entry runs past the
  end), `DirectoryCorrupt` (a damaged central record, an end record that
  points outside the file, a bad manifest), `Unsupported` (above),
  `EntryCorrupt` (bad Deflate data, or a CRC-32 / size / SHA-256 mismatch,
  or a manifest entry the ZIP lacks: checked by `Verify()`, or on each read
  with `VerifyOnRead`). A plain read checks neither CRC nor hash.

## Writing and updating

- `new ZipPackWriter(path)`; `Add(path, bytes, sourceHash, compression)`
  keeps the entry in memory (compressed when it is), and `Commit()` writes
  `path.tmp`, flushes it and renames it over `path` in one step, so readers
  see the old or the new ZIP, never a partial one. A writer disposed
  without `Commit()` leaves no file.
- `ZipPackWriter.Update(path)`: incremental update. `Add` replaces or adds,
  `Remove` drops, and every other entry is copied as its stored bytes (no
  recompression), with its CRC, SHA-256 and source hash. It is a full
  rewrite of the file, about one file copy. `Basis` is the ZIP being
  updated: read its entries' `SourceHash` to decide what to rebuild.
- Compression is chosen per entry (`DefaultCompression`, default stored);
  a Deflate entry that would not shrink is stored.
- **Deterministic**: entries in ordinal path order with the manifest last,
  every timestamp 1980-01-01 00:00, no extra fields unless ZIP64 needs
  them, no comments. Rebuilding unchanged content, in any order or through
  any number of updates, gives the same bytes.
- On Windows the rename fails while the ZIP is open: unmount it before
  committing an update, then mount it again.

## Mods folder and mount order

Everything the game reads comes through one `VirtualFileSystem`: ordered
mounts, highest priority first; a path resolves to the first mount that
has it. Paths are case-insensitive with forward slashes. Reads are
thread-safe; the mount list is swapped whole when it changes.

All content lives in **one `mods/` folder** beside the game's exe; there
is no separate cache folder:

- **System mods**: the content the engine generates from the game's own
  data, `mods/<dataset>.zip` (`skynet.zip`, `shock.zip`). Its entries are
  **stored**, so a read is a plain positioned read with nothing to
  inflate (see the measurements). Mounted **locked**: always on, lowest
  priority, cannot be unmounted (the MODS screen shows them as locked). The
  other data set's system mod is not mounted.
- **User mods**: `mods/<name>.zip` or `mods/<name>/`, mounted above the
  system mods in load order (later wins).
- **One name, one mod**: `mods/foo/` and `mods/foo.zip` are the same mod.
  The folder lies over the ZIP file by file, so a hand edit of a single
  file in the folder overrides the ZIP. The same holds for system mods
  (`mods/<dataset>/` over `mods/<dataset>.zip`).
- Folders whose name starts with a dot are ignored, and so is every file
  that is not a `.zip`.

Priority, highest first:

```
user mod N   (folder, then its zip)
...
user mod 1
system mod   (folder, then its zip)   locked
```

### Unpacking a ZIP

A ZIP is read in place by default (`ZipMode.InPlace`). It can instead be
**unpacked** into its folder `mods/foo/` (`ZipMode.Unpack`,
`ZipUnpack.Unpack`), after which reads come from plain files and the ZIP
remains mounted underneath; worth it for a large Deflate ZIP read often. A
stamp `mods/foo/.unpack-stamp` (hidden from the mount) holds the ZIP's size
and modification time; the ZIP is unpacked again only when they change.
Each file is written to a temporary name and renamed, and the stamp is
written last, so an interrupted unpack is redone. Unpacking overwrites the
files the ZIP carries and keeps any other file in the folder; the engine's
manifest is not unpacked.

## Measurements

### The system mod, full data set

Release build, warm OS cache, best of five; "read all" reads every entry
whole (`ReadAllBytes`), "largest" the largest block alone. Deflate is the
fastest level (what the old pak used for blocks):

| Container | SkyNET MB | open ms | read all ms | largest ms | Future Shock MB | open ms | read all ms | largest ms |
|---|---|---|---|---|---|---|---|---|
| old RPAK, Deflate | 67.0 | 0.08 | 207 | 9.0 | 78.2 | 0.09 | 252 | 18.9 |
| ZIP, Deflate | 67.0 | 0.29 | 202 | 9.1 | 78.3 | 0.19 | 237 | 18.9 |
| ZIP, stored (shipped) | 204.4 | 0.51 | 48 | 1.7 | 224.0 | 0.20 | 50 | 3.1 |

Stored costs three times the disk and reads four to five times faster;
the container makes no difference (RPAK Deflate ≈ ZIP Deflate). Opening
reads the central directory and parses the manifest, well under a
millisecond. Baking writes the stored ZIP in about 0.6 s per data set.

### Synthetic

`ContentBenchmark.cs` (retroeng repo: `tests/RetroEng.Core.Tests/`) (opt-in:
`RETROENG_BENCH=1`). 1000 entries of 1-8 KB and four 4 MB entries,
compressing ~2.5:1 at `Optimal`; warm OS cache, best of four, Release,
milliseconds. One run on a development machine:

| Source | Size MB | Build | Mount | 1000 small | 4 × 4 MB | 1000 small, 8 threads |
|---|---|---|---|---|---|---|
| ZIP, stored | 20.6 | 67 | 2.7 | 2.3 | 3.1 | 5.2 |
| ZIP, Deflate | 18.8 | 498 | 2.7 | 14.9 | 40.7 | 9.6 |
| ZIP by System.IO.Compression, Deflate | 18.7 | 467 | 0.6 | 15.5 | 39.1 | 6.2 |
| folder | - | 250 | 1.1 | 26.6 | 2.9 | 5.8 |

Decompression dominates reading. A stored ZIP is the fastest for small
entries (one handle, no per-file open) and equals a folder for large ones.
Mounting the engine's ZIP costs the manifest's parse (1000 entries here).
The multi-threaded column is dominated by scheduling overhead at these
sizes.
