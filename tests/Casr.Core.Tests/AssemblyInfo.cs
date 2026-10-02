using Xunit;

// Tests mutate shared environment variables and touch per-user agent stores;
// disable xUnit test parallelization to avoid cross-test races.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
