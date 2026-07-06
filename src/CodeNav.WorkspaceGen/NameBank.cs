namespace CodeNav.WorkspaceGen;

/// <summary>
/// Deterministic word banks for generating realistic enterprise identifiers.
/// Owns: vocabulary only. Does not own: any randomness policy (callers pass Random).
/// </summary>
internal static class NameBank
{
    public static readonly string[] Products =
    {
        "Billing", "Payments", "Identity", "Catalog", "Ordering", "Shipping",
        "Inventory", "Reporting", "Notifications", "Search", "Pricing", "Accounts",
        "Documents", "Workflow", "Integration", "Compliance", "Provisioning", "Telemetry",
    };

    public static readonly string[] Subsystems =
    {
        "Invoicing", "Taxation", "Settlement", "Disputes", "Ledger", "Rating",
        "Quoting", "Agreements", "Onboarding", "Verification", "Sessions", "Tokens",
        "Assortment", "Variants", "Media", "Bundles", "Fulfilment", "Returns",
        "Tracking", "Carriers", "Stock", "Replenishment", "Warehousing", "Forecasting",
        "Statements", "Exports", "Digests", "Alerts", "Channels", "Templates",
        "Indexing", "Ranking", "Suggestions", "Synonyms", "Discounts", "Campaigns",
        "Tariffs", "Margins", "Receivables", "Payables", "Journals", "Closing",
        "Archival", "Signatures", "Rendering", "Approvals", "Escalations", "Routing",
        "Connectors", "Mappings", "Webhooks", "Audit", "Retention", "Screening",
        "Licensing", "Tenants", "Quotas", "Metering", "Ingestion", "Aggregation",
    };

    public static readonly string[] Nouns =
    {
        "Invoice", "Payment", "Customer", "Order", "Shipment", "Tax", "Ledger",
        "Account", "Contract", "Quote", "Refund", "Voucher", "Rate", "Tariff",
        "Policy", "Claim", "Batch", "Statement", "Balance", "Transaction", "Receipt",
        "Schedule", "Adjustment", "Allocation", "Reservation", "Manifest", "Parcel",
        "Carrier", "Warehouse", "Product", "Variant", "Bundle", "Segment", "Tenant",
        "Session", "Token", "Credential", "Profile", "Document", "Template", "Report",
        "Digest", "Alert", "Channel", "Route", "Mapping", "Connector", "Snapshot",
        "Journal", "Period", "Threshold", "Quota", "Metric", "Event", "Command",
    };

    public static readonly string[] Verbs =
    {
        "Create", "Update", "Delete", "Get", "Find", "Compute", "Validate",
        "Approve", "Reject", "Submit", "Cancel", "Archive", "Sync", "Import",
        "Export", "Reconcile", "Allocate", "Release", "Suspend", "Resume",
        "Publish", "Consume", "Enqueue", "Dispatch", "Resolve", "Merge", "Split",
        "Apply", "Revert", "Estimate", "Register", "Enrich", "Normalize", "Verify",
    };

    public static readonly string[] ServiceSuffixes =
    {
        "Service", "Manager", "Processor", "Handler", "Coordinator", "Provider",
        "Factory", "Gateway", "Validator", "Calculator", "Builder", "Mapper",
        "Publisher", "Consumer", "Orchestrator", "Resolver", "Selector", "Planner",
    };

    public static readonly string[] RepoSuffixes =
    {
        "Repository", "Store", "Reader", "Writer", "Cache", "Adapter", "Client",
    };

    public static readonly string[] EnumSuffixes =
    {
        "Status", "Kind", "State", "Mode", "Category", "Priority", "Severity",
    };

    public static readonly string[] EnumMembers =
    {
        "None", "Pending", "Active", "Suspended", "Completed", "Failed",
        "Cancelled", "Draft", "Submitted", "Approved", "Rejected", "Archived",
        "Unknown", "Low", "Normal", "High", "Critical",
    };

    public static string Pick(Random rng, string[] bank) => bank[rng.Next(bank.Length)];
}
