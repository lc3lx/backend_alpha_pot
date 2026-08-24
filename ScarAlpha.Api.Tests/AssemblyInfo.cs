using Xunit;

// Strategy configuration — RSI entry levels, the calibration offset, the warmup floor,
// the regime switch — is process-global static state, set once at startup from
// appsettings. Tests that pin one of those values would otherwise leak it into whatever
// else xUnit happened to be running in parallel, producing failures that move around
// between runs and look like real indicator bugs.
//
// Serialising the suite is the honest trade: it costs a few seconds and removes an entire
// class of phantom failure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
