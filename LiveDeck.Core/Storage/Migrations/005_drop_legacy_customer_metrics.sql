-- Phase 3a: drop unused TrustScore + order-tracking columns. P1b's pivot to
-- label workflow eliminated the order-completion lifecycle; these columns
-- were never updated and made the entity heavier than it needs to be.

DROP INDEX IF EXISTS IX_Customer_TrustScore;
ALTER TABLE Customer DROP COLUMN TrustScore;
ALTER TABLE Customer DROP COLUMN TotalOrders;
ALTER TABLE Customer DROP COLUMN CompletedOrders;
ALTER TABLE Customer DROP COLUMN CancelledOrders;

UPDATE _meta SET SchemaVersion = 5 WHERE Id = 1;
