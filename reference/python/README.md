# Python reference engine

An executable specification of the C# engine: same algorithms, same random streams,
same arithmetic order. It exists because the C# code must match something that can be
run and tested anywhere, and it produces the golden files in `../../testdata/golden`
that the C# tests compare against.

    python -m unittest discover -s tests        # 14 tests, stdlib only
    python -m sra.cli verify ../../testdata/synth_500.xer
    python -m sra.cli simulate ../../testdata/synth_500.xer --risk ../../testdata/synth_500.risk.json
    python tools_make_golden.py && python tools_make_golden.py numerics   # regenerate fixtures + golden

Any change to the engine is made in both languages, then golden files are regenerated.
It is ~30x slower than C# and not meant for production runs.
