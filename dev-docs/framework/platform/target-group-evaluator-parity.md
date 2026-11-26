# Target group evaluator: legacy parity

`TargetGroupEvaluatorTask` (`Smartstore.Core/Platform/Identity/Rules`) is a port of the SmartStoreNET task of the same name. It maintains system-generated `CustomerRoleMapping` rows (`IsSystemMapping`) based on the rule sets of customer roles. The port matches legacy behavior except where noted below.

| Audit point | Legacy behavior | Port | Status | Tests |
|---|---|---|---|---|
| Delete scope ordering | System mappings are deleted before the work scope is opened (raw SQL). | `ExecuteDeleteAsync` runs before the `DbContextScope`, filtered on `IsSystemMapping` and the optional `CustomerRoleIds` parameter. | Parity | `Run_deletes_all_system_mappings_for_full_run_but_only_scoped_role_for_CustomerRoleIds_param` |
| Hooks | `hooksEnabled: false`, `validateOnSave: false`, `autoCommit: false`. | EF Core cannot disable hooks entirely. The scope uses `HookImportance.Important` (suppresses Normal-tier hooks only) and `deferCommit`. It was audited that no Important/Essential hook acts on `CustomerRoleMapping` inserts. | **Exception** | `TargetGroupEvaluatorTaskHookAuditTests.No_unvetted_Important_or_Essential_hook_targets_CustomerRoleMapping`, `Scan_covers_every_built_module_assembly` |
| Entity detaching | Best-effort; failures are swallowed. | Same; exceptions in `DetachEntities` are intentionally swallowed. | Parity | Covered indirectly by `Run_creates_correct_mapping_count_at_chunk_boundary` |
| Role loading | Roles loaded untracked with rule sets expanded, before the scope. | `AsNoTracking` + `AsSplitQuery` with `Include`/`ThenInclude` (replaces EF6 `Expand()`), loaded before the scope. | Parity (EF Core equivalent) | `Run_produces_correct_mappings_for_roles_with_zero_one_and_nested_rule_sets`, `Run_skips_inactive_roles_and_roles_with_only_inactive_rule_sets` |
| Cancellation | No token is passed to database calls; cancellation is honored in the loops and returns (does not throw). Cache invalidation is skipped on cancel, even if rows were already deleted or committed. | Same. | Parity | `Run_stops_early_on_cancellation_and_does_not_persist_uncommitted_chunks`, `Run_does_not_persist_anything_when_token_is_already_cancelled` |
| Progress messages | One message per processed role. | Same. | Parity | `Run_reports_progress_exactly_once_per_processed_role_with_expected_message` |
| ACL cache invalidation | Invalidated when `numAdded > 0 \|\| numDeleted > 0`. | Same. | Parity | `Run_invalidates_Acl_cache_only_when_something_was_added_or_deleted` |

Each exception is also marked with an inline code comment in the task.
