using Xunit;

// Serialize headless Avalonia tests (issue #1101): the stock Avalonia.Headless.XUnit harness
// dispatches every test on a single dispatch thread and Avalonia does not support concurrent
// execution against a shared application. DisableTestParallelization removes the last load-driven
// trigger for the cross-thread "a different thread owns it" fault.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true, MaxParallelThreads = 1)]
