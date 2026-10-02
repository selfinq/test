# TTT1 arcade (System 12) static-recompilation workspace

Status: groundwork only. No game data is present and nothing has been recompiled.
See `STATUS.json`, `MISSING_INPUTS.md`, `CONTINUE.md`, `reports/PHASE1_AUDIT.md`.

Verified commands:
    python3 -m unittest tests/test_verify_romset.py -v      # 7 tests pass
    python3 tools/verify_romset.py input/roms/tektagt.zip   # exit 2 until the ZIP is supplied
Base tool (separate checkout, MIT): /home/user/blacklabelhq/recompone @ 8d0091c
    dotnet build RecompOne.sln -c Release                   # succeeded
Do not send AI-generated contributions to BlackLabelHQ (their README rejects them).
Game files, generated code and traces are git-ignored and must stay local.
