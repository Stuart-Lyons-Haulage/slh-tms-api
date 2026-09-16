# Info mailbox API change note

This branch updates Site Master alignment so Info Mailbox orders remain visible in Order Review even when collection or delivery site wording cannot be resolved.

Key behaviour:

- Site Master matching is enrichment-only.
- Unmatched collection/delivery names become intake warnings.
- Collection and delivery aliases include `origin`, `pickupSite`, `pickupLocation`, `dropSite`, `dropLocation`, `collectFrom` and `deliverTo`.
- Resolved Site Master addresses are copied into planner-facing collection/delivery address fields where missing.
