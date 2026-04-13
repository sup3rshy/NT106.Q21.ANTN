# T01 Proof Summary: DrawActionBase + PointData

## Task
Create `NetDraw.Shared/Models/DrawActionBase.cs` with abstract base class for draw action hierarchy.

## Proof Artifacts

| # | Type | File | Status |
|---|------|------|--------|
| 1 | cli  | T01-01-cli.txt | PASS |
| 2 | file | T01-02-file.txt | PASS |

## Notes
- `PointData` and `DashStyle` are NOT duplicated into this file because they already exist in `DrawAction.cs` in the same namespace (`NetDraw.Shared.Models`). The new `DrawActionBase` references `DashStyle` from there. When `DrawAction.cs` is eventually removed, these types will be moved.
- Existing `DrawAction.cs` was NOT modified per task instructions.
- Build passes with 0 warnings, 0 errors.
