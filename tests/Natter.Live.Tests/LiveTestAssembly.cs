using Xunit;

// Live tests share one bot session, one guild channel and one API quota. Running them in
// parallel would make gateway sessions compete and messages from one test leak into another,
// so the live suite is deliberately serial.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
