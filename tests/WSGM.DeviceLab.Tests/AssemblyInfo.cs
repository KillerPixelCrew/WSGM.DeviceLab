// Several suites here drive process-wide state: PluginTestWorkflowSafetyTests and
// TemporaryDirectory force GC.Collect plus WaitForPendingFinalizers in a loop to make a collectible
// plugin load context unload, and a blocking collection suspends every managed thread in the
// process, not only the test that asked for it. Run in parallel with a suite that waits on a
// bounded rendezvous, that stop-the-world work is charged to whichever test happens to be waiting.
// This is the same rule, for the same reason, as WSGM.Tests in the main repository.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
