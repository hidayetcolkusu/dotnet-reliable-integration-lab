using Xunit;

// The integration suite in the "lab" collection owns one SQL Server, one RabbitMQ and one
// database pair, and its workers claim jobs from that shared state. xUnit runs test
// COLLECTIONS in parallel, so the pure classes outside "lab" (CompositionTests,
// ContractBoundaryTests, ErpAttemptBudgetTests, ScriptTests) were racing the lab tests for
// the runner's CPU. The lab's timing-sensitive tests - attempt budgets, leases, the
// lost-response retry - then depend on how much CPU a pure test happens to consume, which
// is exactly the kind of environment sensitivity that made a slow CI runner disagree with
// a local run while the durable state was never wrong.
//
// Run every collection sequentially: these tests prove correctness of durable state, and
// that must not depend on how much of the runner's CPU they happen to share.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
