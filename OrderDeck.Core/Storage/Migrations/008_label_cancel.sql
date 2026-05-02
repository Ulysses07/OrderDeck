-- Soft-cancel support for printed/queued labels.
-- CancelledAt is unix seconds when the cancellation happened; NULL = active.
-- CancelReason is one of the preset codes ("customer", "wrong-product",
-- "duplicate", "out-of-stock", "custom") chosen in CancelLabelDialog;
-- "custom" carries free-form text after a colon, e.g. "custom:siparişin yarısı bozuk".
-- Reports filter on CancelledAt IS NULL when summing revenue / counting items.

ALTER TABLE Label ADD COLUMN CancelledAt   INTEGER NULL;
ALTER TABLE Label ADD COLUMN CancelReason  TEXT    NULL;

UPDATE _meta SET SchemaVersion = 8 WHERE Id = 1;
