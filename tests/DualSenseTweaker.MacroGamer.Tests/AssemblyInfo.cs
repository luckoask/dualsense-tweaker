// Quota tests deliberately burn a worker CPU or exhaust its memory. Run these fixtures sequentially
// so desktop/IPC timing checks do not compete with hostile-script fixtures on two-core Windows CI.
// Individual race/ownership tests still execute their explicit concurrent tasks.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
