using Xunit;

// Every test class in this assembly reads the same directory tree — the whole
// of content/, several thousand files — and one of them writes it: passing
// SW5E_WRITE_CONTENT=1 makes ImportedContentTests regenerate the class graph
// before asserting on it. xUnit runs collections in parallel by default, so
// that regeneration raced the readers and failed on a locked file, which is a
// flake in the one mode a contributor reaches for when they are already
// changing the corpus.
//
// Serialising the assembly is the cheap fix and costs almost nothing: the
// suite is a few seconds of file reading either way, and nothing in it is
// waiting on a network or a database.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
