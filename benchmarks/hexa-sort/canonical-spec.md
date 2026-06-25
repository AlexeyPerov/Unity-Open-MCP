# Hexa Sort — Canonical Game Specification (v1, frozen)

This is the **single source of truth** for the Hexa Sort benchmark level. Prompt
steps implement *this* spec; the rubric scores against *this* spec. It is
deliberately small, deterministic, and machine-checkable so that runs can be
compared across versions.

> **Frozen.** Do not change the numbers, names, seed board, or reference
> solution in this file without bumping the benchmark version. Changing any of
> these invalidates prior run comparisons.

## 1. What the game is

Hexa Sort is a 3D sorting puzzle. The board holds a row of vertical slots. Each
slot is a stack of colored hex pieces. The player moves the top piece of one
slot onto another slot, following legality rules, until every slot is either
empty or holds a single solid color. That final state is the win condition.

## 2. Data model (the agent must implement exactly these)

| Name      | Value | Meaning                                                       |
| --------- | ----- | ------------------------------------------------------------- |
| Colors    | `Red`, `Green`, `Blue` | Fixed palette of three colors.               |
| `SLOT_COUNT` | `4` | The board always has exactly four slots, indexed `0..3`.      |
| `CAP`     | `2`   | Maximum pieces a slot may hold.                               |
| `TARGET`  | `2`   | A slot is *complete* when it holds `TARGET` pieces, all one color. |

Each color appears exactly `TARGET` (= 2) times in the level, for 6 pieces total.

Suggested C# shape (names are normative; internals are the agent's choice):

```csharp
namespace HexaSort {
    public enum HexColor { Red, Green, Blue }

    // A slot is a stack. Bottom piece is index 0; the top is the last element.
    // A slot may never exceed CAP pieces.
    public sealed class HexaBoard {
        public const int SLOT_COUNT = 4;
        public const int CAP = 2;
        public const int TARGET = 2;

        public void LoadSeed();                       // reset to the canonical start board
        public MoveResult Move(int srcSlot, int dstSlot); // move the top piece of src onto dst
        public bool IsSolved { get; }                 // true when solved-state predicate holds
        public int SolvedFiredCount { get; }          // number of times the completion signal fired
        // ... accessors the agent chooses for slot/piece state
    }

    public enum MoveResult {
        Ok, EmptySource, DestFull, ColorMismatch, SameSlot, OutOfRange
    }
}
```

The board model **must** be plain C# (no `UnityEngine` dependency) so it can be
exercised by EditMode unit tests and by the benchmark's validation step without
a running scene.

## 3. Canonical seed board

`LoadSeed()` must produce exactly this board (listed bottom → top):

| Slot | Pieces (bottom → top) | Top color |
| ---- | --------------------- | --------- |
| 0    | `[Red, Green]`        | Green     |
| 1    | `[Red, Blue]`         | Blue      |
| 2    | `[Green, Blue]`       | Blue      |
| 3    | `[]` (empty)          | —         |

Piece counts: Red = 2, Green = 2, Blue = 2. One empty slot.

## 4. Move rule (v1: single top piece)

A move `(srcSlot, dstSlot)` takes the **single top piece** of `src` and places
it on top of `dst`. It is legal only when **all** of the following hold:

1. `srcSlot != dstSlot`.
2. `srcSlot` and `dstSlot` are in `0..SLOT_COUNT-1`.
3. `srcSlot` is non-empty.
4. `dstSlot` has fewer than `CAP` pieces.
5. `dstSlot` is empty **or** the top color of `dstSlot` equals the top color of `srcSlot`.

If any condition fails, `Move` returns the matching non-`Ok` `MoveResult` and
**does not** mutate the board. On success it pops the piece from `src`, pushes
it onto `dst`, and returns `Ok`.

> Multi-piece "same-color run" moves (moving several stacked same-color pieces
> at once) are the fuller Hexa Sort rule but are **out of scope** for v1. The
> canonical level is fully solvable with single-piece moves (see §7).

## 5. Completion / win condition

After a successful `Move`, evaluate the solved-state predicate (§6). The
completion signal fires **exactly once**, at the moment the board transitions
into the solved state as a result of a legal move. Concretely:

- `SolvedFiredCount` starts at `0`.
- It increments to `1` on the move that first makes `IsSolved` true.
- It must **not** increment again on subsequent moves (there are none in the
  reference solution, but the guard must exist).
- It must **not** increment on a partial board, on the seed, or on an illegal
  move.

## 6. Solved-state predicate (machine-checkable)

`IsSolved` is `true` if and only if **every** slot satisfies:

> the slot is empty **or** it holds exactly `TARGET` pieces that are all the
  same color.

Equivalently for the canonical level: exactly three slots are complete
(`[Red,Red]`, `[Green,Green]`, `[Blue,Blue]` in any slot order) and the fourth
slot is empty.

## 7. Reference solution (proven to reach solved state)

Replaying these four moves from `LoadSeed()` must end in `IsSolved == true` and
`SolvedFiredCount == 1`:

| # | Move (`src → dst`) | Board after (bottom → top)                          | Note                  |
| - | ------------------ | --------------------------------------------------- | --------------------- |
| 1 | slot 1 → slot 3    | s0=[R,G] s1=[R] s2=[G,B] s3=[B]                     | Blue onto empty       |
| 2 | slot 2 → slot 3    | s0=[R,G] s1=[R] s2=[G] s3=[B,B] ✓                   | Blue complete         |
| 3 | slot 0 → slot 2    | s0=[R] s1=[R] s2=[G,G] ✓ s3=[B,B]                   | Green complete        |
| 4 | slot 1 → slot 0    | s0=[R,R] ✓ s1=[] s2=[G,G] s3=[B,B]                  | Red complete → SOLVED |

At the end of move 4, `IsSolved` flips to true and the completion signal fires.
Before move 4 the board was not solved (slot 1 held `[Red]`, a non-empty,
non-complete slot).

### Negative cases (must hold)

- `Move(0, 0)` → `SameSlot`.
- `Move(3, 0)` on the seed (slot 3 empty) → `EmptySource`.
- `Move(0, 1)` on the seed (top Green onto top Blue) → `ColorMismatch`.
- `Move` that would overflow a full slot → `DestFull`.

## 8. Scene representation (3D)

- Board lies on the **XZ plane**. Slots are placed along the **+X axis**; slot
  index maps to an increasing X position with fixed spacing.
- Pieces stack along **+Y**; stack index maps to increasing Y.
- Each piece is a 3D primitive. A hexagonal prism is ideal; a cube is
  acceptable for v1. Piece color is conveyed by material color.
- Required root objects (see prompt A-02): `BoardRoot`, `SlotsRoot`, `PiecesRoot`,
  `GameManager`. Slots are children of `SlotsRoot`; live pieces are children of
  `PiecesRoot`.
- Visual input (mouse/touch selection) is **optional** for v1. The benchmark
  drives the game through the board-model API (§2), not through the GUI.

## 9. Out of scope for v1

To keep the level deterministic and the win logic checkable, the following are
explicitly excluded:

- Save / load and persistence.
- Networking or multiplayer.
- Procedural or randomized level generation.
- Multi-piece run moves, cascades, or auto-sort chains.
- Animation, particles, audio, juice, and other polish.
- Multiple levels, level select, or scoring UI.

## 10. Unity placement conventions

- Scripts: `Assets/_Benchmark/HexaSort/`, namespace `HexaSort`.
- Benchmark evidence outputs: `Assets/_Benchmark/Run/` (see `artifacts.md`).
- The board model (`HexaBoard`) must compile in an EditMode-testable assembly
  with no `UnityEngine` references.
